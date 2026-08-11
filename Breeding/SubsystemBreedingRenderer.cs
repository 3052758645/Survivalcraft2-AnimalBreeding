using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Engine.Media;
using GameEntitySystem;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖状态浮动文字渲染器(全局 IDrawable)。
    /// 在每只配置内生物头顶绘制 3 行信息(参考原版 ComponentDisplayHealthAndNameBehavior 的绘制方式)：
    ///   第1行：性别 + 生物显示名(例如 "♂公 灰狼")
    ///   第2行：成长阶段 + 繁殖状态(例如 "幼崽期 | 成长中" / "成年期 | 怀孕中(0.5天)" / "成年期 | 发情中")
    ///   第3行：成长进度百分比 + 文字进度条(例如 "成长 60% [███░░]")
    ///
    /// 性能优化(避免多生物时卡顿)：
    /// · 自己持有 PrimitivesRenderer3D，不共享 SubsystemModelsRenderer 的(避免被其 Clear 误清，也避免时机依赖)。
    /// · DrawOrder=300，晚于 SubsystemModelsRenderer 的 201(模型绘制完毕)，自己 Flush(camera.ViewProjectionMatrix)。
    /// · 世界距离剔除：超过 kMaxDrawDistance 直接跳过，不做视图空间变换。
    /// · 视锥剔除：用 BoundingFrustum.Intersection 判断生物是否在视野内。
    /// · 不每帧 ToList snapshot：通过 SubsystemBreeding.ForEachTracked 直接遍历内部 Dictionary(带锁)。
    /// · 字体批次引用在每帧 Draw 开始时取一次，所有生物共用。
    /// </summary>
    public class SubsystemBreedingRenderer : IDrawable
    {
        // ==================== IDrawable ====================

        /// <summary>
        /// DrawOrder=300：晚于 SubsystemModelsRenderer 的 [-10000, 1, 99, 201]，
        /// 确保所有模型先画完，再画我们的浮动文字(避免被模型遮挡/深度冲突)。
        /// 自己在 Draw 末尾调用 Flush(camera.ViewProjectionMatrix)，不依赖 SubsystemModelsRenderer。
        /// </summary>
        public int[] DrawOrders => m_drawOrders;
        readonly int[] m_drawOrders = { 300 };

        // ==================== 缓存 ====================

        /// <summary>自己持有的 3D 基元渲染器。不复用 SubsystemModelsRenderer 的，避免被 Clear 误清。</summary>
        readonly PrimitivesRenderer3D m_primitivesRenderer = new();

        /// <summary>Pericles 字体。原版浮动文字(如暴击数字、血量百分比)均使用此字体。</summary>
        BitmapFont m_font;

        /// <summary>字体是否加载失败的标记，避免每帧重复尝试加载并打日志。</summary>
        bool m_fontLoadFailed;

        /// <summary>调试：本帧绘制生物计数(由 SubsystemBreeding.LogRenderTick 节流输出)。</summary>
        int m_drawnThisFrame;

        // ==================== 距离剔除参数 ====================

        /// <summary>最大绘制距离(米)。超过此距离的生物不绘制浮动文字。</summary>
        const float kMaxDrawDistance = 24f;

        /// <summary>开始淡出的距离(米)。kFadeStart 内全显，kMaxDrawDistance 外全隐。</summary>
        const float kFadeStart = 16f;

        /// <summary>kMaxDrawDistance 的平方，用于距离平方比较避免开方。</summary>
        const float kMaxDrawDistanceSq = kMaxDrawDistance * kMaxDrawDistance;

        // ==================== IDrawable.Draw ====================

        public void Draw(Camera camera, int drawOrder)
        {
            // 1. 入口检查：繁殖系统未初始化/配置未启用 → 直接返回
            if (!SubsystemBreeding.Initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            // 2. 懒加载字体
            if (m_font == null && !m_fontLoadFailed)
            {
                try
                {
                    m_font = ContentManager.Get<BitmapFont>("Fonts/Pericles");
                }
                catch (Exception e)
                {
                    Log.Warning("[HYKJ.Breeding] 加载字体 Fonts/Pericles 失败: " + e.Message);
                    m_fontLoadFailed = true;
                }
            }
            if (m_font == null) return;

            // 3. 准备本帧共用数据
            m_drawnThisFrame = 0;
            double currentDay = SubsystemBreeding.GetCurrentDay();
            Vector3 camPos = camera.ViewPosition;
            BoundingFrustum frustum = camera.ViewFrustum;
            Matrix viewMatrix = camera.ViewMatrix;
            Vector3 viewDir = camera.ViewDirection;
            Vector3 viewUp = camera.ViewUp;

            // 视图空间 right/down 向量(每帧算一次，所有生物共用)
            Vector3 right = Vector3.TransformNormal(
                0.005f * Vector3.Normalize(Vector3.Cross(viewDir, viewUp)),
                viewMatrix);
            Vector3 down = Vector3.TransformNormal(-0.005f * Vector3.UnitY, viewMatrix);

            // 字体批次引用(每帧取一次，所有生物共用同一个 batch)
            FontBatch3D fontBatch = m_primitivesRenderer.FontBatch(
                m_font, 1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp);

            // 4. 遍历所有被追踪动物，入队浮动文字(不 ToList，直接遍历内部 Dictionary)
            SubsystemBreeding.ForEachTracked((entity, state) =>
            {
                try
                {
                    DrawOneCreature(cfg, currentDay, camPos, frustum, viewMatrix, right, down, fontBatch, entity, state);
                }
                catch (Exception e)
                {
                    // 单只动物绘制异常不影响其他动物(节流日志避免刷屏)
                    Log.Warning($"[Breeding] 渲染单只动物异常: id={entity?.Id}, err={e.Message}");
                }
            });

            // 5. 自己 Flush(用 ViewProjectionMatrix，不依赖 SubsystemModelsRenderer)
            m_primitivesRenderer.Flush(camera.ViewProjectionMatrix);

            // 6. 调试心跳日志(每 300 帧一次)
            SubsystemBreeding.LogRenderTick(m_drawnThisFrame);
        }

        /// <summary>为单只生物入队 3 行浮动文字。</summary>
        void DrawOneCreature(
            BreedingConfig cfg, double currentDay,
            Vector3 camPos, BoundingFrustum frustum, Matrix viewMatrix,
            Vector3 right, Vector3 down, FontBatch3D fontBatch,
            Entity entity, BreedingState state)
        {
            if (entity == null || state == null) return;
            if (!entity.IsAddedToProject) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return;

            ComponentBody body = creature.ComponentBody;
            if (body == null) return;

            // 跳过尸体：ComponentHealth.DeathTime 有值即视为已死亡
            ComponentHealth health = creature.ComponentHealth;
            if (health != null && health.DeathTime.HasValue) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            // —— 性能优化 1：世界距离剔除(平方比较，避免开方) ——
            Vector3 bodyPos = body.Position;
            float dx = bodyPos.X - camPos.X;
            float dy = bodyPos.Y - camPos.Y;
            float dz = bodyPos.Z - camPos.Z;
            float distSq = dx * dx + dy * dy + dz * dz;
            if (distSq > kMaxDrawDistanceSq) return;

            // —— 性能优化 2：视锥剔除(用生物位置点判断，简化但够用) ——
            if (!frustum.Intersection(bodyPos)) return;

            // 头顶世界坐标(参考 ComponentDisplayHealthAndNameBehavior：Position + UnitY*height + (0,0.4,0))
            float height = body.BoxSize.Y;
            Vector3 headPos = new(bodyPos.X, bodyPos.Y + height + 0.4f, bodyPos.Z);
            Vector3 line2Pos = new(bodyPos.X, bodyPos.Y + height + 0.2f, bodyPos.Z);
            Vector3 line3Pos = new(bodyPos.X, bodyPos.Y + height + 0.0f, bodyPos.Z);

            // 转视图空间
            Vector3 vector = Vector3.Transform(headPos, viewMatrix);
            if (vector.Z >= 0f) return; // 在相机后方，剔除

            // 距离淡出：kFadeStart 内全显，kMaxDrawDistance 外全隐
            float dist = MathF.Sqrt(distSq);
            float fade = MathUtils.Saturate((dist - kFadeStart) / (kMaxDrawDistance - kFadeStart));
            Color color = Color.Lerp(Color.White, Color.Transparent, fade);
            if (color.A <= 6) return;

            // ==================== 第1行：性别 + 生物名称 ====================
            // 例如："♂公 灰狼" / "♀母 马"
            string line1 = state.GetGenderDisplayName() + " " + creature.DisplayName;
            fontBatch.QueueText(line1, vector, right, down, color, TextAnchor.HorizontalCenter | TextAnchor.Bottom);

            // ==================== 第2行：成长阶段 + 繁殖状态 ====================
            Vector3 vector2 = Vector3.Transform(line2Pos, viewMatrix);
            if (vector2.Z < 0f)
            {
                string line2 = state.GetStageDisplayName() + " | " + state.GetBreedingStatus(currentDay);
                fontBatch.QueueText(line2, vector2, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }

            // ==================== 第3行：成长进度百分比 + 进度条 ====================
            Vector3 vector3 = Vector3.Transform(line3Pos, viewMatrix);
            if (vector3.Z < 0f)
            {
                float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
                string line3 = "成长 " + ((int)(progress * 100f)).ToString() + "% " + BuildProgressBar(progress);
                fontBatch.QueueText(line3, vector3, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }

            m_drawnThisFrame++;
        }

        /// <summary>生成 6 格 ASCII 进度条。进度 0 → "[░░░░░░]"，进度 1 → "[██████]"。</summary>
        static string BuildProgressBar(float progress)
        {
            const int blocks = 6;
            int filled = (int)Math.Clamp(Math.Round(progress * blocks), 0, blocks);
            return "[" + new string('█', filled) + new string('░', blocks - filled) + "]";
        }
    }
}
