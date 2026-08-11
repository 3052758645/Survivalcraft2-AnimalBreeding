using System;
using Engine;
using GameEntitySystem;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统独立模组的加载入口。
    /// 仅注册繁殖系统所需的 6 个钩子，不依赖荒野科技主模组的任何功能。
    /// 所有逻辑委托给 SubsystemBreeding 静态类。
    /// </summary>
    public class BreedingModLoader : ModLoader
    {
        /// <summary>繁殖状态浮动文字渲染器(单例)。在 OnProjectLoaded 中创建并注册到 SubsystemDrawing。</summary>
        SubsystemBreedingRenderer m_renderer;
        public override void __ModInitialize()
        {
            // 动物繁殖系统相关钩子：实体生命周期、存档读写、每帧更新、攻击力修正
            ModsManager.RegisterHook("OnProjectLoaded", this);
            ModsManager.RegisterHook("OnEntityAdd", this);
            ModsManager.RegisterHook("OnEntityRemove", this);
            ModsManager.RegisterHook("OnReadSpawnData", this);
            ModsManager.RegisterHook("OnSaveSpawnData", this);
            ModsManager.RegisterHook("OnFactorsUpdate", this);
            ModsManager.RegisterHook("OnMinerHit", this);

            Log.Information("[BreedingMod] 动物繁殖系统模组初始化");
        }

        /// <summary>当 Project 加载完成时执行。繁殖系统在此缓存子系统引用 + 加载配置 + 注册渲染器。</summary>
        public override void OnProjectLoaded(Project project)
        {
            SubsystemBreeding.Initialize(project);

            // 注册繁殖状态浮动文字渲染器(IDrawable)
            // 通过 SubsystemDrawing.AddDrawable 把它加入绘制队列；
            // SubsystemDrawing.Load 会自动注册 Project 中所有 IDrawable 子系统，
            // 但本渲染器不是 Subsystem，需要手动 AddDrawable。
            try
            {
                SubsystemDrawing drawing = project.FindSubsystem<SubsystemDrawing>(false);
                if (drawing == null)
                {
                    Log.Warning("[BreedingMod] SubsystemDrawing 未就绪，渲染器延迟到下次 OnProjectLoaded 再注册");
                    return;
                }
                if (m_renderer == null)
                {
                    m_renderer = new SubsystemBreedingRenderer();
                }
                drawing.AddDrawable(m_renderer);
                SubsystemBreeding.RendererRegistered = true;
                Log.Information("[BreedingMod] 繁殖状态浮动文字渲染器已注册到 SubsystemDrawing");
            }
            catch (Exception e)
            {
                Log.Warning("[BreedingMod] 注册渲染器失败: " + e.Message);
            }
        }

        /// <summary>实体被添加时执行。繁殖系统在此注册可繁殖生物的状态。</summary>
        public override void OnEntityAdd(Entity entity)
        {
            SubsystemBreeding.OnEntityAdd(entity);
        }

        /// <summary>实体被移除时执行。繁殖系统在此清理状态。</summary>
        public override void OnEntityRemove(Entity entity)
        {
            SubsystemBreeding.OnEntityRemove(entity);
        }

        /// <summary>读取生物存档数据时执行。繁殖系统在此反序列化 BreedingState。</summary>
        public override void OnReadSpawnData(Entity entity, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnReadSpawnData(entity, spawnEntityData);
        }

        /// <summary>保存生物存档数据时执行。繁殖系统在此序列化 BreedingState 到 SpawnEntityData.Data。</summary>
        public override void OnSaveSpawnData(ComponentSpawn spawn, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnSaveSpawnData(spawn, spawnEntityData);
        }

        /// <summary>
        /// ComponentFactors 每帧更新时执行(玩家与可攻击生物都会触发)。
        /// 繁殖系统在此驱动：成长阶段推进、发情期判定、ChaseRange factor、母体怀孕/分娩/交配。
        /// 注：API 标记此钩子为 Obsolete(建议改用 mod 自带 Component)，但功能仍生效。
        /// </summary>
#pragma warning disable CS0618
        public override void OnFactorsUpdate(ComponentFactors componentFactors, float dt)
        {
            SubsystemBreeding.OnFactorsUpdate(componentFactors, dt);
        }
#pragma warning restore CS0618

        /// <summary>
        /// 近战攻击命中时执行。繁殖系统在此按成长阶段/发情期/残血乘算修正攻击力。
        /// 注：本钩子签名声明 out bool hitted，按 API 约定返回 false 表示不强制改写命中判定。
        /// </summary>
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
    }
}
