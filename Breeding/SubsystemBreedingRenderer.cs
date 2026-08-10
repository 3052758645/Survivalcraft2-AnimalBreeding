using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using GameEntitySystem;
using Game;

namespace HYKJ.Breeding
{
    /// <summary>
    /// 动物繁殖状态浮动文字渲染器。
    /// 在每只配置内生物头顶绘制 3 行信息：
    ///   第1行：性别 + 生物显示名(例如 "♂公 灰狼")
    ///   第2行：成长阶段 + 繁殖状态(例如 "幼崽期 | 成长中" / "成年期 | 怀孕中(0.5天)" / "成年期 | 发情中")
    ///   第3行：成长进度百分比 + 文字进度条(例如 "成长 60% [███░░]")
    ///
    /// 实现说明(参考原版 CorpseManager.Draw 的"视图空间 + ProjectionMatrix Flush"绘制方式)：
    /// · 复用 SubsystemModelsRenderer.PrimitivesRenderer(共享 PrimitivesRenderer3D)。
    /// · 把世界坐标 pos 用 camera.ViewMatrix 变换到视图空间后入队 FontBatch。
    /// · 不在本渲染器内调用 Flush；由 SubsystemModelsRenderer.Draw 在 DrawOrder=201 时
    ///   统一调用 m_primitivesRenderer.Flush(camera.ProjectionMatrix) 把 layer 1 的字体批次一并刷出。
    /// · 本渲染器 DrawOrders = {10}，确保在 SubsystemModelsRenderer 的 DrawOrder=1(刷 layer 0)之后、
    ///   DrawOrder=201(刷 layer 1)之前入队，从而被正确绘制。
    /// </summary>
    public class SubsystemBreedingRenderer : IDrawable
    {
        // ==================== IDrawable ====================

        /// <summary>
        /// DrawOrder 选 10：晚于 SubsystemModelsRenderer 的 -10000(模型准备) 与 1(layer 0 刷出)，
        /// 早于 99 与 201(layer 1 刷出)。这样我们入队的 FontBatch(layer 1) 会被 SubsystemModelsRenderer
        /// 在 DrawOrder=201 时统一 Flush 出来，无需本渲染器自己 Flush。
        /// </summary>
        public int[] DrawOrders => s_drawOrders;
        static readonly int[] s_drawOrders = { 10 };

        // ==================== 缓存 ====================

        /// <summary>共享的 3D 基元渲染器(从 SubsystemModelsRenderer 取)。null 表示尚未绑定。</summary>
        PrimitivesRenderer3D m_primitivesRenderer;

        /// <summary>Pericles 字体。原版浮动文字(如暴击数字、尸体解剖进度)均使用此字体。</summary>
        BitmapFont m_font;

        /// <summary>字体是否加载失败的标记，避免每帧重复尝试加载并打日志。</summary>
        bool m_fontLoadFailed;

        /// <summary>渲染器是否已绑定到 SubsystemModelsRenderer.PrimitivesRenderer(仅用于日志节流)。</summary>
        bool m_rendererBoundLogged;

        /// <summary>调试：每 300 帧输出一次绘制计数(由 SubsystemBreeding.LogRenderTick 节流)。</summary>
        int m_drawnThisFrame;

        // ==================== IDrawable.Draw ====================

        public void Draw(Camera camera, int drawOrder)
        {
            // 1. 入口检查：繁殖系统未初始化/配置未启用 → 直接返回
            if (!SubsystemBreeding.Initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            // 2. 懒加载共享 PrimitivesRenderer3D(来自 SubsystemModelsRenderer)
            if (m_primitivesRenderer == null)
            {
                var modelsRenderer = Project.FindSubsystem<SubsystemModelsRenderer>(false);
                if (modelsRenderer == null)
                {
                    // SubsystemModelsRenderer 尚未就绪(可能在加载早期)，下帧再试
                    return;
                }
                m_primitivesRenderer = modelsRenderer.PrimitivesRenderer;
                if (!m_rendererBoundLogged)
                {
                    Log.Information("[HYKJ.Breeding] 渲染器已绑定 SubsystemModelsRenderer.PrimitivesRenderer");
                    m_rendererBoundLogged = true;
                }
            }

            // 3. 懒加载字体
            if (m_font == null && !m_fontLoadFailed)
            {
                try
                {
                    m_font = ContentManager.Get<BitmapFont>("Fonts/Pericles");
                    Log.Information("[HYKJ.Breeding] 字体已加载: Fonts/Pericles");
                }
                catch (Exception e)
                {
                    Log.Warning("[HYKJ.Breeding] 加载字体 Fonts/Pericles 失败: " + e.Message);
                    m_fontLoadFailed = true;
                }
            }
            if (m_font == null) return;

            // 4. 遍历所有被追踪动物，入队浮动文字
            m_drawnThisFrame = 0;
            double currentDay = SubsystemBreeding.GetCurrentDay();
            List<KeyValuePair<Entity, BreedingState>> snapshot = SubsystemBreeding.GetAllTracked();

            for (int i = 0; i < snapshot.Count; i++)
            {
                try
                {
                    DrawOneCreature(camera, cfg, currentDay, snapshot[i].Key, snapshot[i].Value);
                }
                catch (Exception e)
                {
                    // 单只动物绘制异常不影响其他动物
                    Log.Warning($"[HYKJ.Breeding] 渲染单只动物异常: id={snapshot[i].Key?.Id}, err={e.Message}");
                }
            }

            // 5. 调试心跳日志(每 300 帧一次)
            SubsystemBreeding.LogRenderTick(m_drawnThisFrame);

            // 注意：不在此处 Flush。复用 SubsystemModelsRenderer 的 PrimitivesRenderer，
            //       由 SubsystemModelsRenderer.Draw 在 DrawOrder=201 时统一 Flush(camera.ProjectionMatrix)。
        }

        /// <summary>为单只生物入队 3 行浮动文字。任何步骤失败都直接 return(由调用方捕获异常)。</summary>
        void DrawOneCreature(Camera camera, BreedingConfig cfg, double currentDay, Entity entity, BreedingState state)
        {
            if (entity == null || state == null) return;
            if (!entity.IsAddedToProject) return;

            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;

            // 跳过尸体：ComponentHealth.DeathTime 有值即视为已死亡
            ComponentHealth health = entity.FindComponent<ComponentHealth>();
            if (health != null && health.DeathTime.HasValue) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            // 4.1 计算头顶世界坐标：body.Position + BoxSize.Y + 0.4f
            float height = body.BoxSize.Y;
            Vector3 basePos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.4f, 0f);

            // 4.2 转视图空间
            Vector3 viewPos = Vector3.Transform(basePos, camera.ViewMatrix);
            if (viewPos.Z >= 0f) return; // 在相机后方，剔除

            // 4.3 距离淡出：16m 内全显，19m 外全隐
            float fade = MathUtils.Saturate((viewPos.Length() - 16f) / 3f);
            Color color = Color.Lerp(Color.White, Color.Transparent, fade);
            if (color.A <= 6) return;

            // 4.4 计算视图空间的 right / down 向量(参考 CorpseManager.Draw)
            //     · right = view 空间下相机右方向 × 0.005
            //     · down  = view 空间下 -UnitY × 0.005
            Vector3 right = Vector3.TransformNormal(
                0.005f * Vector3.Normalize(Vector3.Cross(camera.ViewDirection, camera.ViewUp)),
                camera.ViewMatrix);
            Vector3 down = Vector3.TransformNormal(-0.005f * Vector3.UnitY, camera.ViewMatrix);

            // 4.5 字体批次(同一帧同一字体状态共用一个 batch)
            FontBatch3D fontBatch = m_primitivesRenderer.FontBatch(
                m_font, 1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp);

            // ==================== 第1行：性别 + 生物名称 ====================
            // 例如："♂公 灰狼" / "♀母 马"
            string name = $"{state.GetGenderDisplayName()} {creature.DisplayName}";
            fontBatch.QueueText(
                name, viewPos, right, down, color,
                TextAnchor.HorizontalCenter | TextAnchor.Bottom);

            // ==================== 第2行：成长阶段 + 繁殖状态 ====================
            // 例如："幼崽期 | 成长中" / "成年期 | 发情中" / "成年期 | 怀孕中(0.5天)"
            Vector3 viewPos2 = Vector3.Transform(basePos - new Vector3(0f, 0.22f, 0f), camera.ViewMatrix);
            if (viewPos2.Z < 0f)
            {
                string line2 = $"{state.GetStageDisplayName()} | {state.GetBreedingStatus(currentDay)}";
                fontBatch.QueueText(
                    line2, viewPos2, right, down, color * 0.85f,
                    TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }

            // ==================== 第3行：成长进度文字 + 进度条 ====================
            // 例如："成长 60% [███░░]"
            Vector3 viewPos3 = Vector3.Transform(basePos - new Vector3(0f, 0.38f, 0f), camera.ViewMatrix);
            if (viewPos3.Z < 0f)
            {
                float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
                string line3 = $"成长 {progress * 100f:F0}% {BuildProgressBar(progress)}";
                fontBatch.QueueText(
                    line3, viewPos3, right, down, color * 0.85f,
                    TextAnchor.HorizontalCenter | TextAnchor.Bottom);
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
