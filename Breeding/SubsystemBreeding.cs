using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Engine;
using GameEntitySystem;
using Game;
using XmlUtilities;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统核心管理器(简化版)。
    /// 机制：
    /// 1. 发情期：当前季节在物种 BreedingSeasons 内 且 不在虚弱期 → IsInEstrus=true。
    /// 2. 公狼寻路：发情公狼在 SeekRadius 内寻找发情母狼，设路径走向她。
    /// 3. 交配：母狼发情 + MateRadius 内有发情公狼 → 累加相处计时。
    ///    相处达 MatingRequiredProximitySeconds 秒 → 交配：母狼怀孕，双方进入虚弱期。
    /// 4. 分娩：孕期倒计时到 0 → 在母体附近生成幼崽。分娩后母狼进入虚弱期。
    /// 5. 成长：幼崽期 CubDurationDays 天后进阶成年。成长度 0→1 期间体型(BoxSize+ModelScale)线性增长。
    /// 6. 体型：原版BoxSize/ModelScale × scale。scale = lerp(CubBoxScale, 成年scale, 成长度)。
    ///    成年scale：公=AdultMaleBoxScale，母=AdultFemaleBoxScale。
    /// 7. 攻击力：幼崽×CubAttackFactor / 成年×AdultAttackFactor / 公额外×MaleAttackBonus。
    /// 8. 仇恨：幼崽/怀孕母狼 ChaseRange=0(不产生仇恨)；发情期 ×EstrusChaseRangeMultiplier。
    /// </summary>
    public static class SubsystemBreeding
    {
        // ==================== 运行时状态 ====================

        static readonly Dictionary<Entity, BreedingState> s_states = new();

        /// <summary>
        /// 上鞍撤销待恢复队列。
        /// 当原马(处于禁止交互状态)被 RemoveEntity 时(原版上鞍流程会先移除原马再 AddEntity Saddled马)，
        /// 把它的状态+位置暂存到此队列。后续 OnEntityAdd 收到 *_Saddled 实体时按位置+时间窗口匹配，
        /// 匹配成功则撤销上鞍(删 Saddled + 重建原马 + 恢复状态)。
        /// 队列项超过 5 秒未匹配自动清理。
        /// </summary>
        static readonly List<PendingSaddleRevert> s_pendingReverts = new();

        /// <summary>
        /// ProjectXmlLoad 缓存的活体生物状态(EntityId → Base64 JSON)。
        /// 活着的生物(在视野内、未被 Despawn)通过 Project.LoadEntities 恢复，不走 OnReadSpawnData，
        /// 其繁殖状态只存在于内存 s_states，退出世界时会丢失。
        /// 此缓存由 ProjectXmlLoad 钩子从 Project.xml 的 &lt;BreedingModStates&gt; 节点读取，
        /// 在 Initialize backfill 阶段按 EntityId 恢复，backfill 完成后清空。
        /// </summary>
        static readonly Dictionary<int, string> s_xmlCachedStates = new();

        /// <summary>
        /// 当前世界目录路径(Initialize 时缓存，用于 OnProjectDisposed 时保存到单独文件)。
        /// 作为 ProjectXmlSave/OnProjectXmlSaved 钩子不可用时的备选保存路径。
        /// </summary>
        static string s_worldDirectory;

        // ==================== 缓存的子系统 ====================

        static Project s_project;
        static SubsystemCreatureSpawn s_creatureSpawn;
        static SubsystemBodies s_bodies;
        static SubsystemSeasons s_seasons;
        static SubsystemTimeOfDay s_timeOfDay;
        static SubsystemTime s_time;
        static SubsystemModelsRenderer s_modelsRenderer;
        static Random s_random = new();
        static bool s_initialized;

        /// <summary>渲染钩子(OnModelDrawExtra)用它获取 FontBatch 入队悬浮文字。</summary>
        public static SubsystemModelsRenderer ModelsRenderer => s_modelsRenderer;

        /// <summary>体型更新节流计数器(每 60 帧更新一次体型，避免每帧写 BoxSize)。</summary>
        static long s_debugFrameCounter;

        /// <summary>
        /// 实体ID → SpawnEntityData缓存（仅用于存档实体，不用于自然生成）。
        /// 由 ProjectXmlLoad 钩子填充，供 OnEntityAdd 钩子按 EntityId 恢复存档状态。
        /// 自然生成的生物不进入此字典（其 Chunk.SpawnsData 已被消耗清空）。
        /// </summary>
        static readonly Dictionary<int, SpawnEntityData> s_entitySpawnDataCache = new();

        /// <summary>
        /// 由 BreedingModLoader.OnProjectLoaded 调用，缓存子系统引用并加载配置。
        /// 注意：ModLoader 是单例，静态字段跨世界保留，必须在此清空旧世界的残留状态。
        /// </summary>
        public static void Initialize(Project project)
        {
            // 保存 OnReadSpawnData 已缓存的本世界存档状态。
            // OnReadSpawnData 在 Initialize 之前被引擎调用(SubsystemCreatureSpawn.LoadSpawnsData 阶段)，
            // 此时已把反序列化的存档状态(性别/出生日/成长阶段等)缓存到 s_states。
            // 下面 Clear 会清空旧世界残留，所以先保存本世界的存档状态，Clear 后只恢复属于当前项目实体的状态。
            Dictionary<Entity, BreedingState> cachedFromSpawn = s_states.Count > 0
                ? new Dictionary<Entity, BreedingState>(s_states)
                : null;

            // 清空旧世界残留(静态字段跨世界保留，不清空会导致旧 Entity 引用泄漏)
            s_states.Clear();
            s_pendingReverts.Clear();
            s_initialized = false;

            s_project = project;
            s_creatureSpawn = project.FindSubsystem<SubsystemCreatureSpawn>(true);
            s_bodies = project.FindSubsystem<SubsystemBodies>(true);
            s_seasons = project.FindSubsystem<SubsystemSeasons>(true);
            s_timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            s_time = project.FindSubsystem<SubsystemTime>(true);
            s_modelsRenderer = project.FindSubsystem<SubsystemModelsRenderer>(true);

            BreedingConfig.Load();
            BreedingConfig cfg = BreedingConfig.Current;

            // 缓存世界目录路径(用于 OnProjectDisposed 时保存到单独文件)
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            s_worldDirectory = gameInfo?.DirectoryName;

            if (cfg?.Enabled == true)
            {
                Log.Information($"[Breeding] 初始化完成，追踪物种数={cfg.Species.Count}");
            }
            else
            {
                Log.Warning("[Breeding] 配置禁用或加载失败，繁殖系统不生效");
            }
            s_initialized = true;

            // 恢复 OnReadSpawnData 缓存的本世界存档状态(仅限当前项目的实体，过滤旧世界残留)
            if (cachedFromSpawn != null && project.Entities != null)
            {
                foreach (Entity e in project.Entities)
                {
                    if (cachedFromSpawn.TryGetValue(e, out BreedingState s))
                    {
                        s_states[e] = s;
                    }
                }
            }

            // 备选加载：如果 ProjectXmlLoad 钩子未被调用(旧版 DLL 可能不支持)，
            // s_xmlCachedStates 为空。此时直接从 Project.xml 文件读取 BreedingModStates 节点。
            if (s_xmlCachedStates.Count == 0 && cfg?.Enabled == true)
            {
                try
                {
                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.DirectoryName))
                    {
                        string projectXmlPath = Storage.CombinePaths(gameInfo.DirectoryName, "Project.xml");
                        if (Storage.FileExists(projectXmlPath))
                        {
                            Log.Information("[Breeding] ProjectXmlLoad 钩子未缓存数据，尝试直接读取 Project.xml 文件");
                            using (System.IO.Stream stream = Storage.OpenFile(projectXmlPath, OpenFileMode.Read))
                            {
                                XElement projectNode = XmlUtils.LoadXmlFromStream(stream, null, true);
                                LoadXmlStates(projectNode);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"[Breeding] 直接读取 Project.xml 失败: {e.Message}");
                }
            }

            // 备选加载2：如果 Project.xml 中也没有 BreedingModStates(保存钩子未被调用)，
            // 尝试从单独文件 BreedingStates.xml 读取(由 OnProjectDisposed 备选保存)。
            if (s_xmlCachedStates.Count == 0 && cfg?.Enabled == true)
            {
                LoadStatesFromFile();
            }

            // 补注册在 Initialize 之前已 AddEntity 的实体
            // (Project.LoadEntities → OnEntityAdd 在 OnProjectLoaded/Initialize 之前触发，
            //  此时 s_initialized=false 导致 OnEntityAdd 跳过。这里遍历补建)
            // 四种情况：
            //   1. s_states 已有缓存(OnReadSpawnData 在 Initialize 前缓存)：校验模板名 + 补应用体型
            //   2. s_xmlCachedStates 有缓存(活着的生物，Project.xml 持久化)：反序列化 + 校验 + 应用体型
            //   3. s_entitySpawnDataCache 有缓存(Despawn 的存档实体)：反序列化 + 校验 + 应用体型
            //   4. 无任何存档(新生物/首次生成)：按自然生成成体初始化
            if (cfg?.Enabled == true && project.Entities != null)
            {
                int backfilled = 0;
                int hit1 = 0, hit2 = 0, hit3 = 0, hit4 = 0; // 诊断：各情况命中次数
                foreach (Entity existing in project.Entities)
                {
                    ComponentCreature creature = existing.FindComponent<ComponentCreature>();
                    if (creature == null) continue;
                    string tn = existing.ValuesDictionary.DatabaseObject?.Name;
                    if (string.IsNullOrEmpty(tn)) continue;
                    string normTn = NormalizeTemplateName(tn);
                    SpeciesConfig sp = cfg.GetSpecies(normTn);
                    if (sp == null) continue;

                    // 情况1：OnReadSpawnData 已缓存存档状态 → 校验模板名 + 补应用体型
                    if (s_states.TryGetValue(existing, out BreedingState cached))
                    {
                        if (!string.Equals(cached.TemplateName, normTn, StringComparison.Ordinal))
                        {
                            Log.Warning($"[Breeding] 状态模板名不匹配: state={cached.TemplateName}, entity={normTn}，丢弃旧状态");
                            s_states.Remove(existing);
                        }
                        else
                        {
                            CacheAndApplyBoxSize(existing, cached, cfg); // 补缓存 OriginalBoxSize + 应用体型
                            backfilled++;
                            hit1++;
                            continue;
                        }
                    }

                    // 情况2：从 Project.xml 的 <BreedingModStates> 恢复(活着的生物，未走 Despawn/OnReadSpawnData)
                    if (s_xmlCachedStates.TryGetValue(existing.Id, out string xmlData))
                    {
                        BreedingState xmlState = BreedingState.Deserialize(xmlData);
                        if (xmlState != null
                            && string.Equals(xmlState.TemplateName, normTn, StringComparison.Ordinal))
                        {
                            s_states[existing] = xmlState;
                            CacheAndApplyBoxSize(existing, xmlState, cfg);
                            backfilled++;
                            hit2++;
                            continue;
                        }
                        // 反序列化失败或模板名不匹配 → 落入情况3
                        Log.Warning($"[Breeding] 情况2失败: entityId={existing.Id}, template={normTn}, xmlStateNull={xmlState == null}");
                    }

                    // 情况3：从 s_entitySpawnDataCache 恢复(Despawn 的存档实体，通过反射缓存)
                    if (s_entitySpawnDataCache.TryGetValue(existing.Id, out SpawnEntityData spawnData))
                    {
                        BreedingState spawnState = BreedingState.Deserialize(spawnData.Data);
                        if (spawnState != null
                            && !string.IsNullOrEmpty(spawnState.TemplateName)
                            && string.Equals(spawnState.TemplateName, normTn, StringComparison.Ordinal))
                        {
                            s_states[existing] = spawnState;
                            CacheAndApplyBoxSize(existing, spawnState, cfg);
                            backfilled++;
                            hit3++;
                            Log.Information($"[Breeding] backfill 情况3: 从 s_entitySpawnDataCache 恢复实体 #{existing.Id} ({normTn})，性别={spawnState.Gender}");
                            continue;
                        }
                        // 反序列化失败或模板名不匹配 → 清理缓存并落入情况4
                        Log.Warning($"[Breeding] 情况3失败: entityId={existing.Id}, template={normTn}, spawnStateNull={spawnState == null}");
                        s_entitySpawnDataCache.Remove(existing.Id);
                    }

                    // 情况4：无任何存档(新生物/首次生成) → 按自然生成成体初始化
                    BreedingState st = new()
                    {
                        TemplateName = normTn,
                        Gender = s_random.Bool(sp.CubMaleProbability) ? BreedingGender.Male : BreedingGender.Female,
                        Stage = GrowthStage.Adult,
                        BirthDay = s_timeOfDay.Day,
                        PregnancyRemainingSeconds = -1f,
                        WeaknessRemainingSeconds = -1f
                    };
                    s_states[existing] = st;
                    CacheAndApplyBoxSize(existing, st, cfg);
                    backfilled++;
                    hit4++;
                }
                Log.Information($"[Breeding] backfill 完成: 总数={backfilled}, 情况1(OnReadSpawnData)={hit1}, 情况2(XML)={hit2}, 情况3(SpawnDataCache)={hit3}, 情况4(随机)={hit4}, xmlCached={s_xmlCachedStates.Count}, spawnCached={s_entitySpawnDataCache.Count}");
            }

            // backfill 完成，XML 缓存不再需要
            s_xmlCachedStates.Clear();
        }

        // ==================== Project.xml 持久化(活着的生物状态) ====================

        /// <summary>
        /// ProjectXmlLoad 钩子：世界加载时从 Project.xml 读取活体生物的繁殖状态。
        /// 活着的生物(在视野内、未被 Despawn)通过 Project.LoadEntities 恢复，不走 OnReadSpawnData，
        /// 其繁殖状态需通过 Project.xml 的 &lt;BreedingModStates&gt; 节点持久化。
        /// 此方法在 ProjectData 构造(实体创建)之前触发，数据缓存到 s_xmlCachedStates，
        /// 供 Initialize backfill 按 EntityId 恢复。
        /// </summary>
        public static void LoadXmlStates(XElement projectNode)
        {
            s_xmlCachedStates.Clear();
            if (projectNode == null)
            {
                Log.Warning("[Breeding] LoadXmlStates: projectNode 为 null");
                return;
            }

            XElement statesNode = projectNode.Element("BreedingModStates");
            if (statesNode == null)
            {
                Log.Information("[Breeding] LoadXmlStates: Project.xml 中无 BreedingModStates 节点(首次进入或上次保存失败)");
                return;
            }

            int count = 0;
            foreach (XElement stateEl in statesNode.Elements("State"))
            {
                int entityId = XmlUtils.GetAttributeValue(stateEl, "EntityId", 0);
                string data = XmlUtils.GetAttributeValue(stateEl, "Data", string.Empty);
                if (entityId != 0 && !string.IsNullOrEmpty(data))
                {
                    s_xmlCachedStates[entityId] = data;
                    count++;
                }
            }
            Log.Information($"[Breeding] LoadXmlStates: 从 Project.xml 读取 {count} 个活体生物状态");
        }

        /// <summary>
        /// ProjectBeforeSubsystemsAndEntitiesLoad 钩子：在实体创建之后、Subsystem.Load 之前触发。
        /// 在此阶段可以访问 Project 的 SpawnSubsystem 来缓存存档实体的 SpawnEntityData，
        /// 以便在 OnEntityAdd 中正确应用存档的性别/体型等状态。
        /// 时序：AddEntities → BeforeSubsystemsAndEntitiesLoad → Subsystem.Load → LoadEntities → OnProjectLoaded
        /// </summary>
        public static void LoadSpawnEntityDataCache(Project project)
        {
            s_entitySpawnDataCache.Clear();
            s_project = project; // 提前缓存 project 引用
            var spawnSubsystem = project.FindSubsystem<SubsystemSpawn>(true);
            if (spawnSubsystem == null)
            {
                Log.Warning("[Breeding] LoadSpawnEntityDataCache: SubsystemSpawn 为 null");
                return;
            }
            // 通过反射访问 m_spawnEntityDatas（private 字段）
            try
            {
                var fieldInfo = typeof(SubsystemSpawn).GetField("m_spawnEntityDatas",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (fieldInfo != null)
                {
                    var spawnEntityDatas = fieldInfo.GetValue(spawnSubsystem) as Dictionary<int, SpawnEntityData>;
                    if (spawnEntityDatas != null)
                    {
                        foreach (var kvp in spawnEntityDatas)
                        {
                            s_entitySpawnDataCache[kvp.Key] = kvp.Value;
                        }
                        Log.Information($"[Breeding] LoadSpawnEntityDataCache: 从 SubsystemSpawn 缓存 {s_entitySpawnDataCache.Count} 个存档实体数据");
                    }
                }
                else
                {
                    Log.Warning("[Breeding] LoadSpawnEntityDataCache: 无法找到 m_spawnEntityDatas 字段");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] LoadSpawnEntityDataCache: 反射访问失败 - {e.Message}");
            }
        }

        /// <summary>
        /// OnProjectXmlSaved 钩子：世界保存时把活着的生物的繁殖状态写入 Project.xml。
        /// 被 Despawn 的生物已通过 OnSaveSpawnData → SpawnEntityData.Data → SubsystemSpawn.Save 保存，
        /// 不在此处理。此处只处理 s_states 中仍然存活的生物(未被 Despawn)。
        /// </summary>
        public static void SaveXmlStates(XElement projectNode)
        {
            if (projectNode == null)
            {
                Log.Warning("[Breeding] SaveXmlStates: projectNode 为 null");
                return;
            }

            // 移除旧节点(避免重复，ProjectXmlSave 和 OnProjectXmlSaved 都会调用此方法)
            projectNode.Element("BreedingModStates")?.Remove();

            Log.Information($"[Breeding] SaveXmlStates: s_states.Count={s_states.Count}");

            if (s_states.Count == 0) return;

            XElement statesNode = new("BreedingModStates");
            foreach (KeyValuePair<Entity, BreedingState> kv in s_states)
            {
                Entity entity = kv.Key;
                BreedingState state = kv.Value;
                if (entity == null || state == null) continue;

                XElement stateEl = new("State");
                XmlUtils.SetAttributeValue(stateEl, "EntityId", entity.Id);
                XmlUtils.SetAttributeValue(stateEl, "Data", state.Serialize());
                statesNode.Add(stateEl);
            }

            if (statesNode.HasElements)
            {
                projectNode.Add(statesNode);
                Log.Information($"[Breeding] SaveXmlStates: 写入 {statesNode.Elements().Count()} 个活体生物状态到 Project.xml");
            }
            else
            {
                Log.Warning("[Breeding] SaveXmlStates: s_states 非空但无有效条目可写入");
            }
        }

        /// <summary>OnProjectDisposed 钩子：世界卸载时保存活体状态到单独文件 + 清空缓存。</summary>
        public static void ClearXmlCache()
        {
            // 备选保存：如果 ProjectXmlSave/OnProjectXmlSaved 钩子未被调用(旧版 DLL)，
            // 在此把活体生物状态保存到单独文件 BreedingStates.xml。
            // OnProjectDisposed 在 Project.Dispose() 之后触发，但 s_states 仍保留数据
            // (entity.Id 是 int 字段不受 Dispose 影响，state.Serialize() 不依赖 Entity)。
            SaveStatesToFile();
            s_xmlCachedStates.Clear();
            s_entitySpawnDataCache.Clear();
        }

        /// <summary>
        /// 把 s_states 保存到单独文件 BreedingStates.xml(备选保存方案)。
        /// 文件路径：{世界目录}/BreedingStates.xml
        /// </summary>
        static void SaveStatesToFile()
        {
            if (string.IsNullOrEmpty(s_worldDirectory) || s_states.Count == 0)
            {
                Log.Information($"[Breeding] SaveStatesToFile: 跳过(worldDir={s_worldDirectory}, states={s_states.Count})");
                return;
            }

            try
            {
                XElement root = new("BreedingStates");
                int count = 0;
                foreach (KeyValuePair<Entity, BreedingState> kv in s_states)
                {
                    if (kv.Key == null || kv.Value == null) continue;
                    XElement el = new("State");
                    XmlUtils.SetAttributeValue(el, "EntityId", kv.Key.Id);
                    XmlUtils.SetAttributeValue(el, "Data", kv.Value.Serialize());
                    root.Add(el);
                    count++;
                }

                if (count > 0)
                {
                    string path = Storage.CombinePaths(s_worldDirectory, "BreedingStates.xml");
                    using (System.IO.Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    {
                        XmlUtils.SaveXmlToStream(root, stream, null, true);
                    }
                    Log.Information($"[Breeding] SaveStatesToFile: 保存 {count} 个状态到 BreedingStates.xml");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] SaveStatesToFile 失败: {e.Message}");
            }
        }

        /// <summary>
        /// 从单独文件 BreedingStates.xml 读取活体状态(备选加载方案)。
        /// 读取到 s_xmlCachedStates，供 backfill 使用。
        /// </summary>
        static void LoadStatesFromFile()
        {
            if (string.IsNullOrEmpty(s_worldDirectory)) return;

            try
            {
                string path = Storage.CombinePaths(s_worldDirectory, "BreedingStates.xml");
                if (!Storage.FileExists(path))
                {
                    Log.Information("[Breeding] LoadStatesFromFile: BreedingStates.xml 不存在");
                    return;
                }

                using (System.IO.Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                {
                    XElement root = XmlUtils.LoadXmlFromStream(stream, null, true);
                    int count = 0;
                    foreach (XElement el in root.Elements("State"))
                    {
                        int entityId = XmlUtils.GetAttributeValue(el, "EntityId", 0);
                        string data = XmlUtils.GetAttributeValue(el, "Data", string.Empty);
                        if (entityId != 0 && !string.IsNullOrEmpty(data))
                        {
                            s_xmlCachedStates[entityId] = data;
                            count++;
                        }
                    }
                    Log.Information($"[Breeding] LoadStatesFromFile: 从 BreedingStates.xml 读取 {count} 个状态");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] LoadStatesFromFile 失败: {e.Message}");
            }
        }

        // ==================== 实体生命周期钩子 ====================

        public static void OnEntityAdd(Entity entity)
        {
            if (!s_initialized || entity == null) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return;

            string templateName = entity.ValuesDictionary.DatabaseObject?.Name;
            if (string.IsNullOrEmpty(templateName)) return;

            // 上鞍撤销：如果新增的是 *_Saddled 实体且待恢复队列有匹配项 → 撤销上鞍
            if (templateName.EndsWith("_Saddled", StringComparison.Ordinal))
            {
                if (TryConsumePendingRevert(entity, templateName, out PendingSaddleRevert revert))
                {
                    RevertSaddling(entity, revert, cfg);
                    return; // 撤销后该 Saddled 实体已被删除，不再处理
                }
                // 无匹配项 = 正常上鞍(原马不处于禁止状态)，继续按带鞍模板注册
            }

            // 归一化模板名：带鞍的马/驴/骆驼等(*_Saddled)去掉后缀后查找配置
            // 这样带鞍和不带鞍的同类可互通交配，幼崽不带鞍(用 base 模板生成)
            string normalizedTemplate = NormalizeTemplateName(templateName);
            SpeciesConfig species = cfg.GetSpecies(normalizedTemplate);
            if (species == null) return;

            if (s_states.ContainsKey(entity))
            {
                // OnReadSpawnData 已恢复存档状态，这里保留不覆盖
                return;
            }

            // 尝试从存档缓存恢复（EntityId 匹配）
            int entityId = entity.Id;
            if (entityId != 0 && s_entitySpawnDataCache.TryGetValue(entityId, out SpawnEntityData spawnData))
            {
                BreedingState state = BreedingState.Deserialize(spawnData.Data);
                if (state != null && !string.IsNullOrEmpty(state.TemplateName))
                {
                    // 验证模板名匹配（已归一化）
                    if (string.Equals(state.TemplateName, normalizedTemplate, StringComparison.Ordinal))
                    {
                        s_states[entity] = state;
                        CacheAndApplyBoxSize(entity, state, cfg);
                        Log.Information($"[Breeding] OnEntityAdd: 从存档恢复实体 #{entityId} ({normalizedTemplate})，性别={state.Gender}");
                        return;
                    }
                    else
                    {
                        Log.Warning($"[Breeding] OnEntityAdd: 实体 #{entityId} 存档模板名({state.TemplateName})与当前模板({normalizedTemplate})不匹配，使用随机生成");
                    }
                }
                else
                {
                    Log.Warning($"[Breeding] OnEntityAdd: 实体 #{entityId} 存档数据反序列化失败");
                }
                // 清理无效缓存
                s_entitySpawnDataCache.Remove(entityId);
            }

            // 自然生成的成体：默认成年，性别随机(按配置概率)
            // TemplateName 存归一化后的名字(不带 _Saddled)，便于交配匹配和体型查找
            BreedingState state2 = new()
            {
                TemplateName = normalizedTemplate,
                Gender = s_random.Bool(species.CubMaleProbability) ? BreedingGender.Male : BreedingGender.Female,
                Stage = GrowthStage.Adult,
                BirthDay = s_timeOfDay.Day,
                PregnancyRemainingSeconds = -1f,
                WeaknessRemainingSeconds = -1f
            };
            s_states[entity] = state2;

            // 缓存原版 BoxSize/ModelScale 并应用成年体型
            CacheAndApplyBoxSize(entity, state2, cfg);
        }

        /// <summary>
        /// 归一化模板名：去掉 _Saddled 后缀。
        /// 例: "Horse_Black_Saddled" → "Horse_Black"
        /// 非带鞍模板原样返回。
        /// </summary>
        static string NormalizeTemplateName(string templateName)
        {
            if (string.IsNullOrEmpty(templateName)) return templateName;
            const string suffix = "_Saddled";
            if (templateName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return templateName.Substring(0, templateName.Length - suffix.Length);
            }
            return templateName;
        }

        public static void OnEntityRemove(Entity entity)
        {
            if (entity == null) return;

            // 上鞍撤销暂存：仅当被移除的是"活的、处于禁止交互状态、配置了交互拦截的可骑乘物种"时暂存。
            // 过滤条件说明：
            //   1. 物种必须配置了 BlockInteractDuringBreeding 或 BlockInteractDuringCub（否则上鞍不会被拦截，无需暂存）
            //   2. 实体必须处于禁止交互状态（繁殖期或幼崽期）
            //   3. 实体必须是活的（Health 为 null 或 > 0），排除死亡移除（被打死/烧死等不会是上鞍）
            if (s_initialized
                && s_states.TryGetValue(entity, out BreedingState state)
                && s_time != null)
            {
                BreedingConfig cfg = BreedingConfig.Current;
                SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
                if (species != null
                    && (species.BlockInteractDuringBreeding || species.BlockInteractDuringCub)
                    && IsInteractBlocked(state, species)
                    && IsAlive(entity))
                {
                    ComponentBody body = entity.FindComponent<ComponentBody>();
                    if (body != null)
                    {
                        s_pendingReverts.Add(new PendingSaddleRevert
                        {
                            OriginalTemplate = state.TemplateName,
                            Position = body.Position,
                            Rotation = body.Rotation,
                            Velocity = body.Velocity,
                            State = state,
                            QueuedAtSeconds = (float)s_time.GameTime
                        });
                        s_states.Remove(entity);
                        return;
                    }
                }
            }

            s_states.Remove(entity);
        }

        /// <summary>
        /// 判断实体是否存活(用于区分上鞍移除 vs 死亡移除)。
        /// 上鞍时原版检查 componentHealth == null || health > 0f，所以上鞍的实体是活的。
        /// 死亡移除时 Health <= 0 或 DeathTime 有值。
        /// </summary>
        static bool IsAlive(Entity entity)
        {
            if (entity == null) return false;
            ComponentHealth health = entity.FindComponent<ComponentHealth>();
            if (health == null) return true; // 无血量组件 = 不会死亡 = 视为活
            if (health.DeathTime.HasValue) return false; // 已死亡
            return health.Health > 0f;
        }

        // ==================== 上鞍撤销(无 hook，用 OnEntityAdd 撤销法) ====================

        /// <summary>
        /// 判断当前状态是否禁止交互(上鞍+骑乘)。
        /// 繁殖期(发情/怀孕/虚弱) 或 幼崽期，按物种配置决定。
        /// </summary>
        static bool IsInteractBlocked(BreedingState state, SpeciesConfig species)
        {
            if (state == null || species == null) return false;
            if (state.Stage == GrowthStage.Cub && species.BlockInteractDuringCub) return true;
            if (species.BlockInteractDuringBreeding && IsInBreedingState(state)) return true;
            return false;
        }

        /// <summary>是否处于繁殖期(发情/怀孕/虚弱)。</summary>
        static bool IsInBreedingState(BreedingState state)
        {
            if (state == null) return false;
            if (state.IsInEstrus) return true;
            if (state.PregnancyRemainingSeconds > 0f) return true;
            if (state.IsWeak) return true;
            return false;
        }

        /// <summary>
        /// 尝试从待恢复队列消费一个匹配项。
        /// 匹配条件：Saddled 实体位置与暂存位置距离 ≤ 2 格，且暂存时间 ≤ 5 秒。
        /// 匹配后从队列移除。返回 true 表示找到匹配项。
        /// </summary>
        static bool TryConsumePendingRevert(Entity saddledEntity, string saddledTemplate, out PendingSaddleRevert matched)
        {
            matched = null;
            if (s_pendingReverts.Count == 0 || s_time == null) return false;

            // saddledTemplate 形如 "Horse_White_Saddled"，去掉 _Saddled 后缀得到原模板 "Horse_White"
            string expectedOriginal = saddledTemplate.Substring(0, saddledTemplate.Length - "_Saddled".Length);

            ComponentBody body = saddledEntity.FindComponent<ComponentBody>();
            if (body == null) return false;
            Vector3 pos = body.Position;

            float now = (float)s_time.GameTime;
            for (int i = s_pendingReverts.Count - 1; i >= 0; i--)
            {
                PendingSaddleRevert r = s_pendingReverts[i];
                // 过期清理
                if (now - r.QueuedAtSeconds > 5f)
                {
                    s_pendingReverts.RemoveAt(i);
                    continue;
                }
                // 模板匹配 + 位置匹配
                if (!string.Equals(r.OriginalTemplate, expectedOriginal, StringComparison.Ordinal)) continue;
                if (Vector3.Distance(r.Position, pos) > 2f) continue;
                matched = r;
                s_pendingReverts.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 撤销上鞍：删除 Saddled 实体，重建原马模板实体，恢复繁殖状态。
        /// 按配置决定是否退鞍给玩家(ConsumeSaddleOnBlocked=false 时尝试退鞍)。
        /// </summary>
        static void RevertSaddling(Entity saddledEntity, PendingSaddleRevert revert, BreedingConfig cfg)
        {
            try
            {
                // 1. 删除 Saddled 实体
                s_project.RemoveEntity(saddledEntity, true);

                // 2. 重建原马模板实体
                Entity original = DatabaseManager.CreateEntity(s_project, revert.OriginalTemplate, false);
                if (original == null)
                {
                    Log.Warning($"[Breeding] 撤销上鞍失败：无法重建原模板 {revert.OriginalTemplate}");
                    return;
                }
                ComponentBody origBody = original.FindComponent<ComponentBody>(true);
                origBody.Position = revert.Position;
                origBody.Rotation = revert.Rotation;
                origBody.Velocity = revert.Velocity;
                original.FindComponent<ComponentSpawn>(true).SpawnDuration = 0f;
                s_project.AddEntity(original);

                // 3. 恢复繁殖状态(OnEntityAdd 会先按自然生成初始化，这里覆盖回原状态)
                //    注意：AddEntity 后 OnEntityAdd 会被同步调用并注册新状态，我们要在它之后覆盖
                s_states[original] = revert.State;
                CacheAndApplyBoxSize(original, revert.State, cfg);

                // 4. 退鞍处理(如果配置 ConsumeSaddleOnBlocked=false)
                //    原版 OnUse 在调用我们 hook 前已经 RemoveActiveTool(1) 扣了鞍。
                //    当前 mod API 无 OnUse hook，无法在扣鞍前拦截，也无法精确定位操作玩家。
                //    因此 ConsumeSaddleOnBlocked=false 的实际行为是"鞍已扣 + 上鞍被撤销"，
                //    无法真正退鞍。此处仅日志提示。
                SpeciesConfig species = cfg.GetSpecies(revert.OriginalTemplate);
                bool consume = species?.ConsumeSaddleOnBlocked ?? false;
                if (!consume)
                {
                    Log.Warning("[Breeding] ConsumeSaddleOnBlocked=false：原版已扣鞍，mod API 无 OnUse hook 无法退鞍，上鞍已撤销");
                }

            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] 撤销上鞍异常: {e.Message}");
            }
        }

        public static void OnReadSpawnData(Entity entity, SpawnEntityData spawnEntityData)
        {
            if (entity == null || spawnEntityData == null)
            {
                return;
            }

            // 即使配置未加载(s_initialized=false，引擎在 Initialize 之前调用本钩子)，
            // 也要先反序列化并缓存状态，避免 Initialize 的 backfill 用随机值覆盖存档。
            BreedingState state = BreedingState.Deserialize(spawnEntityData.Data);
            if (state == null) return; // Data 为空 = 存档时无状态，留给 OnEntityAdd/backfill 创建默认状态

            s_states[entity] = state;

            // 配置未就绪时无法做模板名校验和体型应用，留给 Initialize backfill 补做
            if (!s_initialized) return;

            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            string templateName = entity.ValuesDictionary.DatabaseObject?.Name;
            if (string.IsNullOrEmpty(templateName)) return;

            // 归一化模板名(带鞍马存档读取时也要归一化)
            string normalizedTemplate = NormalizeTemplateName(templateName);
            if (cfg.GetSpecies(normalizedTemplate) == null) return;

            // 状态模板名与归一化后的实体模板名比较(支持带鞍马存档恢复)
            if (!string.Equals(state.TemplateName, normalizedTemplate, StringComparison.Ordinal))
            {
                Log.Warning($"[Breeding] 状态模板名不匹配: state={state.TemplateName}, entity={normalizedTemplate}，丢弃旧状态");
                s_states.Remove(entity);
                return;
            }
            CacheAndApplyBoxSize(entity, state, cfg);
        }

        public static void OnSaveSpawnData(ComponentSpawn spawn, SpawnEntityData spawnEntityData)
        {
            if (!s_initialized || spawn?.Entity == null || spawnEntityData == null) return;
            if (!s_states.TryGetValue(spawn.Entity, out BreedingState state)) return;
            spawnEntityData.Data = state.Serialize();
        }

        // ==================== 每帧更新(由 OnFactorsUpdate 驱动) ====================

        public static void OnFactorsUpdate(ComponentFactors factors, float dt)
        {
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (factors?.Entity == null) return;

            Entity entity = factors.Entity;
            if (!s_states.TryGetValue(entity, out BreedingState state)) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            // 1. 虚弱期倒计时(公母共用)
            if (state.WeaknessRemainingSeconds > 0f)
            {
                state.WeaknessRemainingSeconds -= dt;
                if (state.WeaknessRemainingSeconds < 0f)
                {
                    state.WeaknessRemainingSeconds = -1f;
                }
            }

            // 1b. 喂食状态倒计时(条件性繁衍用)
            if (state.FedRemainingSeconds > 0f)
            {
                state.FedRemainingSeconds -= dt;
                if (state.FedRemainingSeconds < 0f)
                {
                    state.FedRemainingSeconds = -1f;
                }
            }

            // 2. 发情期判定(成年 + 在季节 + 不在虚弱期 + 喂食条件满足)
            // 条件性繁衍: RequireFeeding=true 时还要求 IsFed(已喂食状态未过期)
            // 幼崽不发情，避免幼崽与成年公狼冲突
            Season currentSeason = s_seasons.Season;
            state.IsInEstrus = state.IsAdult
                && species.ParsedSeasons.Contains(currentSeason)
                && !state.IsWeak
                && (!species.RequireFeeding || state.IsFed);

            // 3. 成长阶段推进
            UpdateGrowth(entity, state, species);

            // 4. 体型随成长度更新(节流，每 60 帧一次)
            UpdateBoxSize(entity, state, species);

            // 5. 仇恨范围 factor(幼崽/怀孕母狼=0，发情期×倍率)
            ApplyChaseRangeFactor(factors, state, species);

            // 6. 性别特定更新
            if (state.Gender == BreedingGender.Female)
            {
                UpdateFemale(entity, state, species, dt);
            }
            else
            {
                UpdateMale(entity, state, species);
            }
        }

        /// <summary>成长阶段推进。幼崽期到达 CubDurationDays 后进阶成年。</summary>
        static void UpdateGrowth(Entity entity, BreedingState state, SpeciesConfig species)
        {
            if (state.Stage != GrowthStage.Cub) return;

            double currentDay = s_timeOfDay.Day;
            double ageDays = currentDay - state.BirthDay;

            if (ageDays >= species.CubDurationDays)
            {
                state.Stage = GrowthStage.Adult;
                // 进阶成年时立即应用一次成年体型
                ApplyBoxSizeByGrowth(entity, state, species, 1f);
            }
        }

        /// <summary>体型更新节流：每 60 帧根据成长度重新计算 BoxSize + ModelScale。</summary>
        static void UpdateBoxSize(Entity entity, BreedingState state, SpeciesConfig species)
        {
            if (state.Stage != GrowthStage.Cub) return; // 成年在进阶时已应用
            if (s_debugFrameCounter++ % 60 != 0) return;

            double currentDay = s_timeOfDay.Day;
            float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
            ApplyBoxSizeByGrowth(entity, state, species, progress);
        }

        /// <summary>
        /// 按成长度计算并应用 BoxSize + ModelScale。
        /// scale = lerp(CubBoxScale, 成年scale, progress)
        /// 成年scale：公=AdultMaleBoxScale，母=AdultFemaleBoxScale。
        /// </summary>
        static void ApplyBoxSizeByGrowth(Entity entity, BreedingState state, SpeciesConfig species, float progress)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!state.OriginalBoxSize.HasValue) return;

            float adultScale = state.Gender == BreedingGender.Male ? species.AdultMaleBoxScale : species.AdultFemaleBoxScale;
            float scale = species.CubBoxScale + (adultScale - species.CubBoxScale) * progress;

            // 碰撞盒
            Vector3 orig = state.OriginalBoxSize.Value;
            body.BoxSize = new Vector3(orig.X * scale, orig.Y * scale, orig.Z * scale);

            // 视觉模型缩放(修复幼崽体型不变的问题)
            ComponentModel model = entity.FindComponent<ComponentModel>();
            if (model != null && state.OriginalModelScale.HasValue)
            {
                model.ModelScale = state.OriginalModelScale.Value * scale;
            }
        }

        /// <summary>缓存原版 BoxSize/ModelScale 并按当前成长度应用体型(OnEntityAdd / OnReadSpawnData 用)。</summary>
        static void CacheAndApplyBoxSize(Entity entity, BreedingState state, BreedingConfig cfg)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!state.OriginalBoxSize.HasValue)
            {
                state.OriginalBoxSize = body.BoxSize;
            }

            ComponentModel model = entity.FindComponent<ComponentModel>();
            if (model != null && !state.OriginalModelScale.HasValue)
            {
                state.OriginalModelScale = model.ModelScale;
            }

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            double currentDay = s_timeOfDay.Day;
            float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
            ApplyBoxSizeByGrowth(entity, state, species, progress);
        }

        // ==================== 母体更新：孕期倒计时 + 相处交配 ====================

        static void UpdateFemale(Entity entity, BreedingState state, SpeciesConfig species, float dt)
        {
            // 1. 孕期倒计时
            if (state.PregnancyRemainingSeconds > 0f)
            {
                state.PregnancyRemainingSeconds -= dt;
                if (state.PregnancyRemainingSeconds <= 0f)
                {
                    state.PregnancyRemainingSeconds = -1f;
                    GiveBirth(entity, state, species);
                    state.PregnancyFatherId = 0;
                    // 分娩后进入虚弱期
                    state.WeaknessRemainingSeconds = species.WeaknessSeconds;
                }
                return; // 怀孕中不交配
            }

            // 2. 不在发情期 → 重置相处计时，跳过交配
            if (!state.IsInEstrus)
            {
                state.MatingProximitySeconds = 0f;
                return;
            }

            // 3. 寻找附近发情成年公体(MateRadius 内)
            Entity mate = FindNearbyEstrusMale(entity, state, species);
            if (mate == null)
            {
                state.MatingProximitySeconds = 0f;
                return;
            }

            // 4. 累加相处计时
            state.MatingProximitySeconds += dt;

            // 5. 相处时间达到阈值 → 交配
            if (state.MatingProximitySeconds >= species.MatingRequiredProximitySeconds)
            {
                state.PregnancyRemainingSeconds = species.GestationSeconds;
                state.PregnancyFatherId = mate.Id;
                state.MatingProximitySeconds = 0f;
                // 母狼不进入虚弱期，直接怀孕(怀孕期间不会再次交配)
                // 分娩后才进入虚弱期

                // 只有公狼进入虚弱期(防止一公多母)
                if (s_states.TryGetValue(mate, out BreedingState maleState))
                {
                    maleState.WeaknessRemainingSeconds = species.WeaknessSeconds;
                    maleState.IsInEstrus = false; // 立即更新，防止同帧其他母狼找到他
                    maleState.TargetFemaleId = 0;
                }

                Log.Information($"[Breeding] 交配成功(相处{species.MatingRequiredProximitySeconds}秒): mother={state.TemplateName}#{entity.Id}, father#{mate.Id}, gestationSec={species.GestationSeconds}, maleWeaknessSec={species.WeaknessSeconds}");
            }
        }

        /// <summary>查找 MateRadius 内的发情成年公体(同物种或别名互通)。额外检查 IsWeak 防止同帧多次交配。</summary>
        static Entity FindNearbyEstrusMale(Entity entity, BreedingState state, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = species.MateRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);
            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Male) continue;
                if (!otherState.IsAdult) continue;
                if (otherState.IsWeak) continue; // 虚弱期公狼不可交配(双重保险)
                if (!otherState.IsInEstrus) continue;
                if (!IsMatingCompatible(species, otherState.TemplateName)) continue; // 别名互通
                Vector3 otherPos = results.Array[i].Position;
                if (Vector3.Distance(pos, otherPos) > radius) continue;
                return other;
            }
            return null;
        }

        /// <summary>
        /// 判断当前物种是否可与 targetTemplateName 交配(同物种或别名互通)。
        /// 例: Cow.MatingSet={Cow,Bull}，Bull.MatingSet={Bull,Cow}，二者有交集即可交配。
        /// </summary>
        static bool IsMatingCompatible(SpeciesConfig species, string targetTemplateName)
        {
            if (species == null || string.IsNullOrEmpty(targetTemplateName)) return false;
            return species.MatingSet.Contains(targetTemplateName);
        }

        // ==================== 公体更新：寻找母狼 + 竞争打斗 ====================

        /// <summary>
        /// 发情公狼逻辑：
        /// 1. 在 SeekRadius 内寻找最近的发情母狼，记录 TargetFemaleId。
        /// 2. 检查是否有其他公狼也以同一母狼为目标 → 竞争对手。
        /// 3. 有竞争对手 → 通过 ComponentChaseBehavior.Attack 攻击对方(公狼间矛盾)。
        /// 4. 无竞争对手 → 设路径走向母狼。
        /// </summary>
        static void UpdateMale(Entity entity, BreedingState state, SpeciesConfig species)
        {
            // 不在发情期 → 清除目标，不寻路
            if (!state.IsInEstrus)
            {
                state.TargetFemaleId = 0;
                return;
            }

            // 寻找最近的发情母狼
            Entity female = FindNearestEstrusFemale(entity, state, species);
            if (female == null)
            {
                state.TargetFemaleId = 0;
                return;
            }

            state.TargetFemaleId = female.Id;

            // 检查是否有竞争对手(其他公狼也以同一母狼为目标)
            Entity rival = FindRival(entity, state, female.Id, species);
            if (rival != null)
            {
                // 有竞争对手 → 攻击对方
                ComponentCreature rivalCreature = rival.FindComponent<ComponentCreature>();
                ComponentChaseBehavior chaseBehavior = entity.FindComponent<ComponentChaseBehavior>();
                if (rivalCreature != null && chaseBehavior != null)
                {
                    // 攻击竞争对手(范围=SeekRadius，追击时间=RivalChaseTime秒，非持久)
                    chaseBehavior.Attack(rivalCreature, species.SeekRadius, species.RivalChaseTime, false);
                }
                return;
            }

            // 无竞争对手 → 设路径走向母狼
            ComponentBody femaleBody = female.FindComponent<ComponentBody>();
            if (femaleBody == null) return;

            ComponentPathfinding pathfinding = entity.FindComponent<ComponentPathfinding>();
            if (pathfinding == null) return;

            pathfinding.SetDestination(
                femaleBody.Position,
                1f,            // speed
                1f,            // range
                0,             // maxPathfindingPositions
                true,          // useRandomMovements
                false,         // ignoreHeightDifference
                true,          // raycastDestination
                femaleBody     // doNotAvoidBody(不避开母狼)
            );
        }

        /// <summary>
        /// 查找竞争对手：在同一 SeekRadius 内，有其他发情公狼也以 targetFemaleId 为目标。
        /// </summary>
        static Entity FindRival(Entity entity, BreedingState state, int targetFemaleId, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = species.SeekRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);

            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Male) continue;
                if (!otherState.IsAdult) continue;
                if (!otherState.IsInEstrus) continue;
                if (otherState.TargetFemaleId != targetFemaleId) continue; // 同一目标母狼
                if (!IsMatingCompatible(species, otherState.TemplateName)) continue; // 别名互通
                return other; // 找到竞争对手
            }
            return null;
        }

        /// <summary>查找 SeekRadius 内最近的发情成年母狼(同模板，未怀孕)。</summary>
        static Entity FindNearestEstrusFemale(Entity entity, BreedingState state, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = species.SeekRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);

            Entity nearest = null;
            float nearestDist = float.MaxValue;

            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Female) continue;
                if (!otherState.IsAdult) continue;
                if (otherState.IsWeak) continue; // 虚弱期母狼不可交配
                if (!otherState.IsInEstrus) continue;
                if (otherState.PregnancyRemainingSeconds > 0f) continue; // 跳过怀孕母狼
                if (!IsMatingCompatible(species, otherState.TemplateName)) continue; // 别名互通

                Vector3 otherPos = results.Array[i].Position;
                float dist = Vector3.Distance(pos, otherPos);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = other;
                }
            }
            return nearest;
        }

        // ==================== 分娩 ====================

        /// <summary>
        /// 分娩：在母体附近生成 1 只幼崽。
        /// 用母体模板生成(保证外观一致)，出生后通过 BoxSize+ModelScale 缩小为幼崽尺寸。
        /// 幼崽性别随机；成长后公狼像公狼(大)，母狼像母狼(小)——由 AdultMaleBoxScale/AdultFemaleBoxScale 决定。
        /// </summary>
        static void GiveBirth(Entity mother, BreedingState motherState, SpeciesConfig species)
        {
            ComponentBody motherBody = mother.FindComponent<ComponentBody>();
            if (motherBody == null) return;

            Vector3 basePos = motherBody.Position;
            float off = species.BirthSpawnOffset;
            Vector3 offset = new(s_random.Float(-off, off), 0f, s_random.Float(-off, off));
            Vector3 spawnPos = basePos + offset;

            // 选择幼崽模板(优先级: CubTemplates权重表 > CubTemplateOverride > 沿用母体)
            // CubTemplates: 按权重随机选，如 Cow 配 {"Cow":1,"Bull":1} → 50%生Cow 50%生Bull
            // CubTemplateOverride: 固定模板，如 Cow 配 "Cow" → 永远生 Cow
            // 默认: 沿用母体模板
            string cubTemplate = ChooseCubTemplate(species, motherState.TemplateName);
            Entity cub = s_creatureSpawn.SpawnCreature(cubTemplate, spawnPos, false);
            if (cub == null)
            {
                Log.Warning("[Breeding] 幼崽生成失败");
                return;
            }

            // 修正幼崽的繁殖状态(OnEntityAdd 已按"自然生成成体"初始化，需覆盖)
            if (s_states.TryGetValue(cub, out BreedingState cubState))
            {
                cubState.Stage = GrowthStage.Cub;
                cubState.BirthDay = s_timeOfDay.Day;
                cubState.Gender = s_random.Bool(species.CubMaleProbability) ? BreedingGender.Male : BreedingGender.Female;
                cubState.PregnancyRemainingSeconds = -1f;
                cubState.PregnancyFatherId = 0;
                cubState.MatingProximitySeconds = 0f;
                cubState.WeaknessRemainingSeconds = -1f;

                // 立即应用幼崽体型(成长度=0 → CubBoxScale)
                ApplyBoxSizeByGrowth(cub, cubState, species, 0f);
            }
            Log.Information($"[Breeding] 分娩成功: mother={motherState.TemplateName}#{mother.Id}, cub#{cub.Id}, cubTemplate={cubTemplate}, cubGender={(s_states.TryGetValue(cub, out var cs) ? cs.GetGenderDisplayName() : "?")}");
        }

        /// <summary>
        /// 选择幼崽模板。优先级：CubTemplates权重表 > CubTemplateOverride > 沿用母体。
        /// CubTemplates 按权重随机(如 {"Cow":1,"Bull":1} → 50%/50%)。
        /// </summary>
        static string ChooseCubTemplate(SpeciesConfig species, string motherTemplate)
        {
            // 1. CubTemplates 权重表
            if (species.CubTemplates != null && species.CubTemplates.Count > 0)
            {
                float totalWeight = 0f;
                foreach (var kv in species.CubTemplates) totalWeight += kv.Value;
                if (totalWeight > 0f)
                {
                    float r = s_random.Float(0f, totalWeight);
                    float cum = 0f;
                    foreach (var kv in species.CubTemplates)
                    {
                        cum += kv.Value;
                        if (r <= cum) return kv.Key;
                    }
                    // 浮点精度兜底
                    return species.CubTemplates.Last().Key;
                }
            }
            // 2. CubTemplateOverride 固定模板
            if (!string.IsNullOrEmpty(species.CubTemplateOverride))
            {
                return species.CubTemplateOverride;
            }
            // 3. 沿用母体模板
            return motherTemplate;
        }

        // ==================== 攻击力与 ChaseRange ====================

        /// <summary>
        /// 攻击力修正(乘算)：
        /// · 幼崽 ×CubAttackFactor / 成年 ×AdultAttackFactor
        /// · 公狼额外 ×MaleAttackBonus(母狼为1.0)
        /// </summary>
        public static void OnMinerHit(ComponentMiner miner, ComponentBody target, ref float attackPower)
        {
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (miner?.Entity == null) return;

            Entity attacker = miner.Entity;
            if (!s_states.TryGetValue(attacker, out BreedingState state)) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            float stageFactor = state.Stage == GrowthStage.Cub ? species.CubAttackFactor : species.AdultAttackFactor;
            float genderFactor = state.Gender == BreedingGender.Male ? species.MaleAttackBonus : 1.0f;
            attackPower *= stageFactor * genderFactor;
        }

        // ==================== 骑乘拦截(ScoreMount hook) ====================

        /// <summary>
        /// 骑乘拦截：当玩家试图骑乘处于禁止交互状态(繁殖期/幼崽期)的生物时返回 -1 阻止。
        /// 由 BreedingModLoader.ScoreMount 调用。
        /// </summary>
        public static void OnScoreMount(ComponentRider rider, ComponentMount mount, out float? score)
        {
            score = null;
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (mount?.Entity == null) return;

            Entity mountEntity = mount.Entity;
            if (!s_states.TryGetValue(mountEntity, out BreedingState state)) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            if (IsInteractBlocked(state, species))
            {
                score = -1f; // 返回负分阻止骑乘
            }
        }

        // ==================== 喂食发情(OnEatPickable hook) ====================

        /// <summary>
        /// 生物吃掉落物时触发(由 BreedingModLoader.OnEatPickable 调用)。
        /// 此钩子在生物吃完物品(Count 已扣减)后触发，无法阻止吃，但可据此标记"已喂食"。
        /// 逻辑：
        /// 1. 仅处理被繁殖系统追踪 + RequireFeeding=true 的物种。
        /// 2. 若 FeedItem 为空 = 接受任何食物；否则匹配方块索引(+可选数据)。
        /// 3. 匹配成功 → 设 FedRemainingSeconds = FedDurationSeconds，使该个体可发情。
        /// 注: dealed 始终返回 false，不影响其他模组的喂食钩子。
        /// </summary>
        public static void OnEatPickable(ComponentEatPickableBehavior eatPickableBehavior, Pickable eatPickable, out bool dealed)
        {
            dealed = false;
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (eatPickableBehavior?.Entity == null || eatPickable == null) return;

            Entity entity = eatPickableBehavior.Entity;
            if (!s_states.TryGetValue(entity, out BreedingState state)) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null || !species.RequireFeeding) return;

            // 匹配喂食物品
            if (!IsFeedItemMatch(species, eatPickable)) return;

            // 喂食成功：设置已喂食状态
            state.FedRemainingSeconds = species.FedDurationSeconds;
        }

        /// <summary>
        /// 判断被吃掉的物品是否匹配物种配置的 FeedItem。
        /// · ParsedFeedBlockIndex == null → FeedItem 为空，接受任何食物。
        /// · ParsedFeedBlockIndex < 0 → 解析失败，不匹配任何物品。
        /// · ParsedFeedBlockIndex >= 0 → 比较方块索引；若 ParsedFeedBlockData 非 null 还要比较数据。
        /// </summary>
        static bool IsFeedItemMatch(SpeciesConfig species, Pickable eatPickable)
        {
            if (!species.ParsedFeedBlockIndex.HasValue) return true; // FeedItem 为空 = 接受任何食物
            if (species.ParsedFeedBlockIndex.Value < 0) return false; // 解析失败

            int value = eatPickable.Value;
            int blockId = Terrain.ExtractContents(value);
            if (blockId != species.ParsedFeedBlockIndex.Value) return false;

            // 若配置了数据约束，还要匹配数据
            if (species.ParsedFeedBlockData.HasValue)
            {
                int data = Terrain.ExtractData(value);
                if (data != species.ParsedFeedBlockData.Value) return false;
            }
            return true;
        }

        /// <summary>
        /// 应用仇恨范围 factor(每帧重新 Add Factor)。
        /// · 幼崽：ChaseRange=0(不产生仇恨)
        /// · 怀孕母狼：ChaseRange=0(不产生仇恨)
        /// · 发情期(非虚弱)：ChaseRange ×EstrusChaseRangeMultiplier
        /// · 其他：无额外 factor(正常仇恨)
        /// </summary>
        static void ApplyChaseRangeFactor(ComponentFactors factors, BreedingState state, SpeciesConfig species)
        {
            try
            {
                if (!factors.OtherFactors.TryGetValue("ChaseRange", out List<ComponentLevel.Factor> list))
                {
                    list = new List<ComponentLevel.Factor>();
                    factors.OtherFactors["ChaseRange"] = list;
                }

                // 幼崽不产生仇恨
                if (state.Stage == GrowthStage.Cub)
                {
                    list.Add(new ComponentLevel.Factor
                    {
                        Name = "Breeding.Cub",
                        Value = 0f,
                        FactorAdditionType = FactorAdditionType.Multiply,
                        Description = "幼崽不产生仇恨"
                    });
                    return;
                }

                // 怀孕母狼不产生仇恨
                if (state.Gender == BreedingGender.Female && state.PregnancyRemainingSeconds > 0f)
                {
                    list.Add(new ComponentLevel.Factor
                    {
                        Name = "Breeding.Pregnant",
                        Value = 0f,
                        FactorAdditionType = FactorAdditionType.Multiply,
                        Description = "怀孕母狼不产生仇恨"
                    });
                    return;
                }

                // 发情期仇恨范围倍率
                if (state.IsInEstrus)
                {
                    list.Add(new ComponentLevel.Factor
                    {
                        Name = "Breeding.Estrus",
                        Value = species.EstrusChaseRangeMultiplier,
                        FactorAdditionType = FactorAdditionType.Multiply,
                        Description = "发情期仇恨范围 ×" + species.EstrusChaseRangeMultiplier
                    });
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] ApplyChaseRangeFactor 失败: " + e.Message);
            }
        }

        // ==================== 调试/查询 ====================

        public static int TrackedCount => s_states.Count;

        public static bool Initialized => s_initialized && BreedingConfig.Current?.Enabled == true;

        public static double GetCurrentDay()
        {
            return s_timeOfDay != null ? s_timeOfDay.Day : 0.0;
        }

        /// <summary>查询某实体的繁殖状态(渲染钩子 OnModelRendererDrawExtra 用)。无则返回 null。</summary>
        public static BreedingState GetState(Entity entity)
        {
            return entity != null && s_states.TryGetValue(entity, out BreedingState s) ? s : null;
        }
    }

    /// <summary>
    /// 上鞍撤销待恢复项。
    /// 当禁止交互的原马被上鞍(原版 RemoveEntity+AddEntity Saddled)时，
    /// 暂存其状态+位置，等 Saddled 实体 OnEntityAdd 时按位置匹配撤销。
    /// </summary>
    class PendingSaddleRevert
    {
        public string OriginalTemplate;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public BreedingState State;
        public float QueuedAtSeconds;
    }
}
