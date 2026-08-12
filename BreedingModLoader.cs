using System;
using Engine;
using Engine.Graphics;
using Engine.Media;
using GameEntitySystem;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统独立模组的加载入口。
    /// 仅注册繁殖系统所需的钩子，不依赖荒野科技主模组的任何功能。
    /// 所有逻辑委托给 SubsystemBreeding 静态类。
    ///
    /// 浮动文字渲染：通过 OnModelDrawExtra 钩子(ComponentModel.DrawExtras 回调)实现。
    /// 该钩子对所有 ComponentModel(蒙皮 + 非蒙皮)都会触发，
    /// 因此能覆盖原版 .dae 模型与第三方 glTF/PBR 蒙皮模型(如 HC 模组的生物)。
    /// 用 SubsystemBreeding.ModelsRenderer.PrimitivesRenderer.FontBatch(...).QueueText(...) 入队文字(layer 1)，
    /// 由 SubsystemModelsRenderer 在 DrawOrder=201 时统一 Flush，不需要自己 Flush。
    /// </summary>
    public class BreedingModLoader : ModLoader
    {
        public override void __ModInitialize()
        {
            // 动物繁殖系统相关钩子：实体生命周期、存档读写、每帧更新、攻击力修正、模型绘制扩展
            ModsManager.RegisterHook("OnProjectLoaded", this);
            ModsManager.RegisterHook("OnEntityAdd", this);
            ModsManager.RegisterHook("OnEntityRemove", this);
            ModsManager.RegisterHook("OnReadSpawnData", this);
            ModsManager.RegisterHook("OnSaveSpawnData", this);
            ModsManager.RegisterHook("OnFactorsUpdate", this);
            ModsManager.RegisterHook("OnMinerHit", this);
            ModsManager.RegisterHook("OnModelDrawExtra", this);
            ModsManager.RegisterHook("ScoreMount", this);
            ModsManager.RegisterHook("OnEatPickable", this);

            Log.Information("[BreedingMod] 动物繁殖系统模组初始化(含 OnModelDrawExtra 渲染钩子 + OnEatPickable 喂食钩子)");
        }

        /// <summary>当 Project 加载完成时执行。繁殖系统在此缓存子系统引用 + 加载配置。</summary>
        public override void OnProjectLoaded(Project project)
        {
            SubsystemBreeding.Initialize(project);
        }

        // ==================== 实体生命周期 ====================

        public override void OnEntityAdd(Entity entity)
        {
            SubsystemBreeding.OnEntityAdd(entity);
        }

        public override void OnEntityRemove(Entity entity)
        {
            SubsystemBreeding.OnEntityRemove(entity);
        }

        public override void OnReadSpawnData(Entity entity, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnReadSpawnData(entity, spawnEntityData);
        }

        public override void OnSaveSpawnData(ComponentSpawn spawn, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnSaveSpawnData(spawn, spawnEntityData);
        }

#pragma warning disable CS0618
        public override void OnFactorsUpdate(ComponentFactors componentFactors, float dt)
        {
            SubsystemBreeding.OnFactorsUpdate(componentFactors, dt);
        }
#pragma warning restore CS0618

        public override void OnMinerHit(ComponentMiner miner,
            ComponentBody componentBody,
            Vector3 hitPoint,
            Vector3 hitDirection,
            ref float attackPower,
            ref float playerProbability,
            ref float creatureProbability,
            out bool hitted)
        {
            hitted = false;
            SubsystemBreeding.OnMinerHit(miner, componentBody, ref attackPower);
        }

        // ==================== 骑乘拦截 ====================

        public override void ScoreMount(ComponentRider componentRider, ComponentMount componentMount, out float? score)
        {
            SubsystemBreeding.OnScoreMount(componentRider, componentMount, out score);
        }

        // ==================== 喂食发情 ====================

        /// <summary>
        /// 生物吃掉落物时触发。委托给 SubsystemBreeding 处理"喂食发情"逻辑。
        /// 此钩子在生物吃完物品(Count 已扣减)后触发，用于标记该个体为"已喂食"。
        /// </summary>
        public override void OnEatPickable(ComponentEatPickableBehavior eatPickableBehavior, Pickable eatPickable, out bool dealed)
        {
            SubsystemBreeding.OnEatPickable(eatPickableBehavior, eatPickable, out dealed);
        }

        // ==================== 浮动文字渲染(OnModelDrawExtra 对蒙皮+非蒙皮模型均触发) ====================

        /// <summary>
        /// 每个 ComponentModel 绘制完毕后由 ComponentModel.DrawExtras 回调。
        /// 在此为被追踪的繁殖生物入队 3 行浮动文字 + 1 个图形进度条：
        ///   第1行：性别 + 生物显示名(例如 "♂公 灰狼")
        ///   第2行：成长阶段 + 繁殖状态(例如 "幼崽期 | 成长中" / "成年期 | 怀孕中(0.5天)")
        ///   第3行：成长进度百分比(例如 "成长 60%")
        ///   第4行：图形进度条(FlatBatch3D 画矩形，背景灰 + 前景绿按进度填充)
        ///
        /// 文字用 SubsystemBreeding.ModelsRenderer.PrimitivesRenderer.FontBatch(layer=1) 入队，
        /// 进度条用 FlatBatch(layer=1) 画矩形，均由 SubsystemModelsRenderer 在 DrawOrder=201 时统一 Flush。
        /// </summary>
        public override void OnModelDrawExtra(ComponentModel componentModel, Camera camera, out bool skip)
        {
            skip = false;
            if (!SubsystemBreeding.Initialized) return;

            SubsystemModelsRenderer modelsRenderer = SubsystemBreeding.ModelsRenderer;
            if (modelsRenderer == null) return;

            Entity entity = componentModel?.Entity;
            if (entity == null) return;

            // 只处理被繁殖系统追踪的生物(非繁殖生物/玩家/船等直接跳过)
            BreedingState state = SubsystemBreeding.GetState(entity);
            if (state == null) return;

            BreedingConfig cfg = BreedingConfig.Current;
            SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
            if (species == null) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return;
            ComponentBody body = creature.ComponentBody;
            if (body == null) return;

            // 跳过尸体
            ComponentHealth health = creature.ComponentHealth;
            if (health != null && health.DeathTime.HasValue) return;

            // 头顶世界坐标(参考原版 ComponentDisplayHealthAndNameBehavior)
            float height = body.BoxSize.Y;
            Vector3 headPos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.4f, 0f);
            Vector3 line2Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.2f, 0f);
            Vector3 line3Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.0f, 0f);

            // 转视图空间
            Vector3 vector = Vector3.Transform(headPos, camera.ViewMatrix);
            if (vector.Z >= 0f) return; // 在相机后方

            // 距离淡出：16m 内全显，19m 外全隐
            float fade = MathUtils.Saturate((vector.Length() - 16f) / 3f);
            Color color = Color.Lerp(Color.White, Color.Transparent, fade);
            if (color.A <= 6) return;

            // 视图空间 right/down 向量(参考原版 OnModelRendererDrawExtra)
            Vector3 right = Vector3.TransformNormal(
                0.005f * Vector3.Normalize(Vector3.Cross(camera.ViewDirection, camera.ViewUp)),
                camera.ViewMatrix);
            Vector3 down = Vector3.TransformNormal(-0.005f * Vector3.UnitY, camera.ViewMatrix);

            // 用原版同款字体(LabelWidget.BitmapFont)
            BitmapFont font = LabelWidget.BitmapFont;
            double currentDay = SubsystemBreeding.GetCurrentDay();

            // 字体批次(layer 1，由 SubsystemModelsRenderer 在 DrawOrder=201 统一 Flush)
            FontBatch3D fontBatch = modelsRenderer.PrimitivesRenderer.FontBatch(
                font, 1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp);

            // ==================== 第1行：性别 + 生物名称 ====================
            string line1 = state.GetGenderDisplayName() + " " + creature.DisplayName;
            fontBatch.QueueText(line1, vector, right, down, color, TextAnchor.HorizontalCenter | TextAnchor.Bottom);

            // ==================== 第2行：成长阶段 + 繁殖状态 ====================
            Vector3 vector2 = Vector3.Transform(line2Pos, camera.ViewMatrix);
            if (vector2.Z < 0f)
            {
                string line2 = state.GetStageDisplayName() + " | " + state.GetBreedingStatus(species);
                fontBatch.QueueText(line2, vector2, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }

            // ==================== 第3行：成长进度百分比 ====================
            Vector3 vector3 = Vector3.Transform(line3Pos, camera.ViewMatrix);
            if (vector3.Z < 0f)
            {
                float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
                int percent = (int)Math.Round(progress * 100f);
                string line3 = "成长 " + percent.ToString() + "%";
                fontBatch.QueueText(line3, vector3, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);

                // ==================== 第4行：图形进度条(FlatBatch3D 画矩形) ====================
                // 在百分比文字下方画一个真正的矩形进度条(背景灰 + 前景绿按进度填充)，
                // 不再依赖字符拼进度条，渲染效果稳定。
                DrawProgressBar(modelsRenderer, vector3, right, down, progress, color);
            }
        }

        /// <summary>
        /// 用 FlatBatch3D.QueueQuad 在视图空间绘制矩形进度条。
        /// 布局(均以 right/down 为视图空间单位向量，与文字行高对齐)：
        ///   - 条宽 = 12 个文字单位，条高 = 1.4 个文字单位
        ///   - 条位于基准点 vector3 下方 2 个单位处(避免与百分比文字重叠)
        ///   - 背景灰半透明矩形 + 前景绿色矩形(宽度 = 总宽 × progress)
        /// 颜色乘以 baseColor 实现与文字一致的远距离淡出。
        /// </summary>
        static void DrawProgressBar(SubsystemModelsRenderer modelsRenderer,
            Vector3 vector3, Vector3 right, Vector3 down,
            float progress, Color baseColor)
        {
            FlatBatch3D flatBatch = modelsRenderer.PrimitivesRenderer.FlatBatch(
                1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend);

            const float barWidth = 12f;      // 进度条总宽(文字单位)
            const float barHeight = 1.4f;    // 进度条高度(文字单位)
            const float offsetY = 2f;        // 相对百分比文字下移量(避免重叠)
            float halfW = barWidth * 0.5f;

            // 进度条中心位于 vector3 正下方 offsetY 个单位
            Vector3 center = vector3 + down * offsetY;

            // 背景矩形(灰半透明)：左上 / 右上 / 左下 / 右下
            Color bgColor = new Color(40, 40, 40, 180) * baseColor;
            Vector3 bgTL = center + right * -halfW + down * 0f;
            Vector3 bgTR = center + right *  halfW + down * 0f;
            Vector3 bgBL = center + right * -halfW + down * barHeight;
            Vector3 bgBR = center + right *  halfW + down * barHeight;
            flatBatch.QueueQuad(bgTL, bgTR, bgBL, bgBR, bgColor);

            // 前景矩形(绿色，宽度 = 总宽 × progress)
            if (progress <= 0f) return;
            float filledW = barWidth * Math.Clamp(progress, 0f, 1f);
            // 前景左对齐：从背景左边缘开始向右填充
            Color fgColor = new Color(80, 200, 80, 220) * baseColor;
            Vector3 fgTL = center + right * -halfW + down * 0f;
            Vector3 fgTR = center + right * (-halfW + filledW) + down * 0f;
            Vector3 fgBL = center + right * -halfW + down * barHeight;
            Vector3 fgBR = center + right * (-halfW + filledW) + down * barHeight;
            flatBatch.QueueQuad(fgTL, fgTR, fgBL, fgBR, fgColor);
        }
    }
}
