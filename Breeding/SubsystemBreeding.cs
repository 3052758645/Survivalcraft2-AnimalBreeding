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
    /// 1. 发情期：当前季节在物种 BreedingSeasons 内 → IsInEstrus=true。
    /// 2. 交配：母体发情 + 附近有发情成年公体 → 怀孕(PregnancyRemainingSeconds=GestationSeconds)。
    /// 3. 分娩：孕期倒计时到 0 → 在母体附近生成幼崽(用母体模板，缩小 BoxSize)。
    /// 4. 成长：幼崽期 CubDurationDays 天后进阶成年。成长度 0→1 期间体型线性增长。
    /// 5. 体型：原版BoxSize × scale。scale = lerp(CubBoxScale, 成年scale, 成长度)。
    ///    成年scale：公=AdultMaleBoxScale，母=AdultFemaleBoxScale。
    /// 6. 攻击力：幼崽×CubAttackFactor / 成年×AdultAttackFactor / 公额外×MaleAttackBonus。
    /// 7. 发情期仇恨范围 ×EstrusChaseRangeMultiplier。
    /// </summary>
    public static class SubsystemBreeding
    {
        // ==================== 运行时状态 ====================

        static readonly Dictionary<Entity, BreedingState> s_states = new();

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
        /// </summary>
        public static void Initialize(Project project)
        {
            Log.Information("[Breeding] Initialize 开始");
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
                Log.Information($"[Breeding] 初始化完成，追踪物种数={cfg.Species.Count}，GestationSeconds={cfg.GestationSeconds}");
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
            if (cfg.GetSpecies(templateName) == null) return;

            if (s_states.ContainsKey(entity))
            {
                Log.Information($"[Breeding] OnEntityAdd 已存在状态: id={entity.Id}, template={templateName}");
                return;
            }

            // 自然生成的成体：默认成年，性别随机
            BreedingState state = new()
            {
                TemplateName = templateName,
                Gender = s_random.Bool(0.5f) ? BreedingGender.Male : BreedingGender.Female,
                Stage = GrowthStage.Adult,
                BirthDay = s_timeOfDay.Day,
                PregnancyRemainingSeconds = -1f
            };
            s_states[entity] = state;

            // 缓存原版 BoxSize 并应用成年体型
            CacheAndApplyBoxSize(entity, state, cfg);

            Log.Information($"[Breeding] OnEntityAdd 注册新个体: id={entity.Id}, template={templateName}, gender={state.GetGenderDisplayName()}, stage={state.GetStageDisplayName()}, totalTracked={s_states.Count}");
        }

        public static void OnEntityRemove(Entity entity)
        {
            if (entity == null) return;
            bool removed = s_states.Remove(entity);
            if (removed)
            {
                Log.Information($"[Breeding] OnEntityRemove 清理: id={entity.Id}, totalTracked={s_states.Count}");
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

            Log.Information($"[Breeding] OnReadSpawnData 恢复状态: id={entity.Id}, template={templateName}, gender={state.GetGenderDisplayName()}, stage={state.GetStageDisplayName()}, pregnancySec={state.PregnancyRemainingSeconds}");
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

            // 1. 发情期判定
            Season currentSeason = s_seasons.Season;
            state.IsInEstrus = species.ParsedSeasons.Contains(currentSeason);

            // 2. 成长阶段推进
            UpdateGrowth(entity, state, species, cfg);

            // 3. 体型随成长度更新(节流，每 60 帧一次)
            UpdateBoxSize(entity, state, cfg, species);

            // 4. 发情期 ChaseRange factor
            ApplyChaseRangeFactor(factors, state, cfg);

            // 5. 母体：孕期倒计时 + 交配
            if (state.Gender == BreedingGender.Female)
            {
                UpdateFemale(entity, state, species, cfg, dt);
            }
        }

        /// <summary>成长阶段推进。幼崽期到达 CubDurationDays 后进阶成年。</summary>
        static void UpdateGrowth(Entity entity, BreedingState state, SpeciesConfig species, BreedingConfig cfg)
        {
            if (state.Stage != GrowthStage.Cub) return;

            double currentDay = s_timeOfDay.Day;
            double ageDays = currentDay - state.BirthDay;

            if (ageDays >= species.CubDurationDays)
            {
                state.Stage = GrowthStage.Adult;
                Log.Information($"[Breeding] 幼崽进阶成年: id={entity.Id}, template={state.TemplateName}, age={ageDays:F2}天, cubDuration={species.CubDurationDays}天");
                // 进阶成年时立即应用一次成年体型
                ApplyBoxSizeByGrowth(entity, state, cfg, 1f);
            }
        }

        /// <summary>体型更新节流：每 60 帧根据成长度重新计算 BoxSize。</summary>
        static void UpdateBoxSize(Entity entity, BreedingState state, BreedingConfig cfg, SpeciesConfig species)
        {
            if (state.Stage != GrowthStage.Cub) return; // 成年在进阶时已应用
            if (s_debugFrameCounter++ % 60 != 0) return;

            double currentDay = s_timeOfDay.Day;
            float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
            ApplyBoxSizeByGrowth(entity, state, cfg, progress);
        }

        /// <summary>
        /// 按成长度计算并应用 BoxSize。
        /// scale = lerp(CubBoxScale, 成年scale, progress)
        /// 成年scale：公=AdultMaleBoxScale，母=AdultFemaleBoxScale。
        /// </summary>
        static void ApplyBoxSizeByGrowth(Entity entity, BreedingState state, BreedingConfig cfg, float progress)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!state.OriginalBoxSize.HasValue) return;

            float adultScale = state.Gender == BreedingGender.Male ? cfg.AdultMaleBoxScale : cfg.AdultFemaleBoxScale;
            float scale = cfg.CubBoxScale + (adultScale - cfg.CubBoxScale) * progress;

            Vector3 orig = state.OriginalBoxSize.Value;
            body.BoxSize = new Vector3(orig.X * scale, orig.Y * scale, orig.Z * scale);
        }

        /// <summary>缓存原版 BoxSize 并按当前成长度应用体型(OnEntityAdd / OnReadSpawnData 用)。</summary>
        static void CacheAndApplyBoxSize(Entity entity, BreedingState state, BreedingConfig cfg)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!state.OriginalBoxSize.HasValue)
            {
                state.OriginalBoxSize = body.BoxSize;
            }
            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            double currentDay = s_timeOfDay.Day;
            float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
            ApplyBoxSizeByGrowth(entity, state, cfg, progress);
        }

        // ==================== 母体更新：孕期倒计时 + 交配 ====================

        static void UpdateFemale(Entity entity, BreedingState state, SpeciesConfig species, BreedingConfig cfg, float dt)
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
                }
                return; // 怀孕中不再交配
            }

            // 2. 不在发情期 → 跳过交配
            if (!state.IsInEstrus) return;

            // 3. 寻找附近发情成年公体
            Entity mate = FindMate(entity, state, cfg);
            if (mate == null) return;

            // 4. 交配成功：开始怀孕
            state.PregnancyRemainingSeconds = cfg.GestationSeconds;
            state.PregnancyFatherId = mate.Id;
            Log.Information($"[Breeding] 交配成功: mother={state.TemplateName}#{entity.Id}, father#{mate.Id}, gestationSec={cfg.GestationSeconds}");
        }

        /// <summary>查找附近发情成年公体(同模板)。</summary>
        static Entity FindMate(Entity entity, BreedingState state, BreedingConfig cfg)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = cfg.MateRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);
            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Male) continue;
                if (!otherState.IsAdult) continue;
                if (!otherState.IsInEstrus) continue; // 公体也要发情
                if (!string.Equals(otherState.TemplateName, state.TemplateName, StringComparison.Ordinal)) continue;
                Vector3 otherPos = results.Array[i].Position;
                if (Vector3.Distance(pos, otherPos) > radius) continue;
                return other;
            }
            return null;
        }

        // ==================== 分娩 ====================

        /// <summary>
        /// 分娩：在母体附近生成 1 只幼崽。
        /// 用母体模板生成(保证外观一致)，出生后通过 BoxSize 缩小为幼崽尺寸。
        /// 幼崽性别随机；成长后公狼像公狼(大)，母狼像母狼(小)——由 AdultMaleBoxScale/AdultFemaleBoxScale 决定。
        /// </summary>
        static void GiveBirth(Entity mother, BreedingState motherState, SpeciesConfig species)
        {
            ComponentBody motherBody = mother.FindComponent<ComponentBody>();
            if (motherBody == null) return;

            Vector3 basePos = motherBody.Position;
            Vector3 offset = new(s_random.Float(-1.5f, 1.5f), 0f, s_random.Float(-1.5f, 1.5f));
            Vector3 spawnPos = basePos + offset;

            // 用母体模板生成幼崽(外观与母体一致，BoxSize 由繁殖系统缩小)
            Entity cub = s_creatureSpawn.SpawnCreature(motherState.TemplateName, spawnPos, false);
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
                cubState.Gender = s_random.Bool(0.5f) ? BreedingGender.Male : BreedingGender.Female;
                cubState.PregnancyRemainingSeconds = -1f;
                cubState.PregnancyFatherId = 0;

                // 立即应用幼崽体型(成长度=0 → CubBoxScale)
                ApplyBoxSizeByGrowth(cub, cubState, BreedingConfig.Current, 0f);
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

            float stageFactor = state.Stage == GrowthStage.Cub ? cfg.CubAttackFactor : cfg.AdultAttackFactor;
            float genderFactor = state.Gender == BreedingGender.Male ? cfg.MaleAttackBonus : 1.0f;
            attackPower *= stageFactor * genderFactor;

            if (s_debugHitCounter++ % 200 == 0)
            {
                Log.Information($"[Breeding] OnMinerHit 攻击力修正: id={attacker.Id}, template={state.TemplateName}, stage={state.GetStageDisplayName()}, gender={state.GetGenderDisplayName()}, factor=stage×{stageFactor}*gender×{genderFactor}={stageFactor * genderFactor}");
            }
        }

        /// <summary>应用发情期仇恨范围倍率(每帧重新 Add Factor)。</summary>
        static void ApplyChaseRangeFactor(ComponentFactors factors, BreedingState state, BreedingConfig cfg)
        {
            try
            {
                if (!factors.OtherFactors.TryGetValue("ChaseRange", out List<ComponentLevel.Factor> list))
                {
                    list = new List<ComponentLevel.Factor>();
                    factors.OtherFactors["ChaseRange"] = list;
                }
                if (state.IsInEstrus)
                {
                    list.Add(new ComponentLevel.Factor
                    {
                        Name = "Breeding.Estrus",
                        Value = cfg.EstrusChaseRangeMultiplier,
                        FactorAdditionType = FactorAdditionType.Multiply,
                        Description = "发情期仇恨范围 ×" + cfg.EstrusChaseRangeMultiplier
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
}
