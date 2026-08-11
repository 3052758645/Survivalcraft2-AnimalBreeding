using System;
using System.Collections.Generic;
using Engine;
using GameEntitySystem;
using Game;

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

        // ==================== 缓存的子系统 ====================

        static Project s_project;
        static SubsystemCreatureSpawn s_creatureSpawn;
        static SubsystemBodies s_bodies;
        static SubsystemSeasons s_seasons;
        static SubsystemTimeOfDay s_timeOfDay;
        static SubsystemTime s_time;
        static Random s_random = new();
        static bool s_initialized;

        /// <summary>攻击力修正命中节流计数器(每 200 次命中输出一次)。</summary>
        static long s_debugHitCounter;

        /// <summary>体型更新节流计数器(每 60 帧更新一次体型，避免每帧写 BoxSize)。</summary>
        static long s_debugFrameCounter;

        /// <summary>
        /// 由 BreedingModLoader.OnProjectLoaded 调用，缓存子系统引用并加载配置。
        /// 注意：ModLoader 是单例，静态字段跨世界保留，必须在此清空旧世界的残留状态。
        /// </summary>
        public static void Initialize(Project project)
        {
            Log.Information("[Breeding] Initialize 开始");
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
            Log.Information($"[Breeding] 子系统已缓存: creatureSpawn={s_creatureSpawn!=null}, bodies={s_bodies!=null}, seasons={s_seasons!=null}, timeOfDay={s_timeOfDay!=null}, time={s_time!=null}");

            BreedingConfig.Load();
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled == true)
            {
                Log.Information($"[Breeding] 初始化完成，追踪物种数={cfg.Species.Count}");
            }
            else
            {
                Log.Warning("[Breeding] 配置禁用或加载失败，繁殖系统不生效");
            }
            s_initialized = true;
            Log.Information("[Breeding] Initialize 完成");
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
                // 无匹配项 = 正常上鞍(原马不处于禁止状态)，按 Saddled 模板继续注册
            }

            SpeciesConfig species = cfg.GetSpecies(templateName);
            if (species == null) return;

            if (s_states.ContainsKey(entity))
            {
                Log.Information($"[Breeding] OnEntityAdd 已存在状态: id={entity.Id}, template={templateName}");
                return;
            }

            // 自然生成的成体：默认成年，性别随机(按配置概率)
            BreedingState state = new()
            {
                TemplateName = templateName,
                Gender = s_random.Bool(species.CubMaleProbability) ? BreedingGender.Male : BreedingGender.Female,
                Stage = GrowthStage.Adult,
                BirthDay = s_timeOfDay.Day,
                PregnancyRemainingSeconds = -1f,
                WeaknessRemainingSeconds = -1f
            };
            s_states[entity] = state;

            // 缓存原版 BoxSize/ModelScale 并应用成年体型
            CacheAndApplyBoxSize(entity, state, cfg);

            Log.Information($"[Breeding] OnEntityAdd 注册新个体: id={entity.Id}, template={templateName}, gender={state.GetGenderDisplayName()}, stage={state.GetStageDisplayName()}, totalTracked={s_states.Count}");
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
                            QueuedAtSeconds = s_time.GameTime
                        });
                        Log.Information($"[Breeding] 暂存禁止交互原马待恢复: template={state.TemplateName}#{entity.Id}, stage={state.GetStageDisplayName()}, pos={body.Position}");
                        s_states.Remove(entity);
                        return;
                    }
                }
            }

            bool removed = s_states.Remove(entity);
            if (removed)
            {
                Log.Information($"[Breeding] OnEntityRemove 清理: id={entity.Id}, totalTracked={s_states.Count}");
            }
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

            float now = s_time.GameTime;
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

                Log.Information($"[Breeding] 撤销上鞍成功: original={revert.OriginalTemplate}, stage={revert.State.GetStageDisplayName()}, consumeSaddle={consume}, totalTracked={s_states.Count}");
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] 撤销上鞍异常: {e.Message}");
            }
        }

        public static void OnReadSpawnData(Entity entity, SpawnEntityData spawnEntityData)
        {
            if (!s_initialized || entity == null || spawnEntityData == null) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            string templateName = entity.ValuesDictionary.DatabaseObject?.Name;
            if (string.IsNullOrEmpty(templateName) || cfg.GetSpecies(templateName) == null) return;

            BreedingState state = BreedingState.Deserialize(spawnEntityData.Data);
            if (state == null) return;

            if (!string.Equals(state.TemplateName, templateName, StringComparison.Ordinal))
            {
                Log.Warning($"[Breeding] 状态模板名不匹配: state={state.TemplateName}, entity={templateName}，丢弃旧状态");
                return;
            }
            s_states[entity] = state;
            CacheAndApplyBoxSize(entity, state, cfg);

            Log.Information($"[Breeding] OnReadSpawnData 恢复状态: id={entity.Id}, template={templateName}, gender={state.GetGenderDisplayName()}, stage={state.GetStageDisplayName()}, pregnancySec={state.PregnancyRemainingSeconds}, weaknessSec={state.WeaknessRemainingSeconds}");
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
                    Log.Information($"[Breeding] 虚弱期结束: id={entity.Id}, template={state.TemplateName}, gender={state.GetGenderDisplayName()}");
                }
            }

            // 2. 发情期判定(成年 + 在季节 + 不在虚弱期)
            // 幼崽不发情，避免幼崽与成年公狼冲突
            Season currentSeason = s_seasons.Season;
            state.IsInEstrus = state.IsAdult
                && species.ParsedSeasons.Contains(currentSeason)
                && !state.IsWeak;

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
                Log.Information($"[Breeding] 幼崽进阶成年: id={entity.Id}, template={state.TemplateName}, age={ageDays:F2}天, cubDuration={species.CubDurationDays}天");
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
                    Log.Information($"[Breeding] 公狼竞争: #{entity.Id} 攻击 #{rival.Id}，目标母狼#{female.Id}");
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

            // 用母体模板或 CubTemplateOverride 生成幼崽
            // 默认沿用母体(外观一致)；若配置了 CubTemplateOverride 则用指定模板(如 Cow 母牛生 Cow 小牛)
            string cubTemplate = !string.IsNullOrEmpty(species.CubTemplateOverride)
                ? species.CubTemplateOverride
                : motherState.TemplateName;
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
            Log.Information($"[Breeding] 分娩成功: mother={motherState.TemplateName}#{mother.Id}, cub#{cub.Id}, cubGender={(s_states.TryGetValue(cub, out var cs) ? cs.GetGenderDisplayName() : "?")}");
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

            if (s_debugHitCounter++ % 200 == 0)
            {
                Log.Information($"[Breeding] OnMinerHit 攻击力修正: id={attacker.Id}, template={state.TemplateName}, stage={state.GetStageDisplayName()}, gender={state.GetGenderDisplayName()}, factor=stage×{stageFactor}*gender×{genderFactor}={stageFactor * genderFactor}");
            }
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
                Log.Information($"[Breeding] 阻止骑乘: mount={state.TemplateName}#{mountEntity.Id}, stage={state.GetStageDisplayName()}, status={state.GetBreedingStatus()}");
            }
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
