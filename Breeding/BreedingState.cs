using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameEntitySystem;
using Engine;
using Game;

namespace Game
{
    /// <summary>
    /// 生物性别。公与母的判定用于交配；其余属性(攻击力等)与性别无关，由成长阶段与状态决定。
    /// </summary>
    public enum BreedingGender
    {
        Male = 0,
        Female = 1
    }

    /// <summary>
    /// 成长阶段。幼崽期 → 成年期；不同成长阶段生物模型与碰撞大小不一样，属性随成长而增强。
    /// </summary>
    public enum GrowthStage
    {
        Cub = 0,
        Adult = 1
    }

    /// <summary>
    /// 单只动物的繁殖运行时状态。
    /// 该对象不挂在实体上(避免修改 API 实体模板)，而是由 SubsystemBreeding 用 Dictionary 缓存。
    /// 持久化通过 SpawnEntityData.Data(JSON 字符串)走 OnSaveSpawnData / OnReadSpawnData 钩子。
    /// </summary>
    public class BreedingState
    {
        /// <summary>该状态所属实体的模板名(如 Wolf_Gray)。用于查找 SpeciesConfig。</summary>
        public string TemplateName { get; set; }

        /// <summary>性别。公/母。新建个体时随机分配(各 50%)。</summary>
        public BreedingGender Gender { get; set; }

        /// <summary>成长阶段。新建个体默认为 Cub，成年后切到 Adult。</summary>
        public GrowthStage Stage { get; set; } = GrowthStage.Cub;

        /// <summary>出生时刻(游戏天，SubsystemTimeOfDay.Day)。</summary>
        public double BirthDay { get; set; }

        /// <summary>上次进阶到成年的时刻(游戏天)。出生即为 Cub 时此值为 -1。</summary>
        public double AdultDay { get; set; } = -1.0;

        /// <summary>父亲实体 Id(用于近亲检测)。0 表示未知(自然生成)。</summary>
        public int FatherId { get; set; }

        /// <summary>母亲实体 Id(用于近亲检测)。0 表示未知(自然生成)。</summary>
        public int MotherId { get; set; }

        /// <summary>最近成功交配的对象实体 Id 列表(最多 RecentMatesLimit 个，FIFO)。用于重复配对检测。
        /// 用 List 而非 Queue 以保证 JSON 序列化/反序列化稳定。</summary>
        public List<int> RecentMates { get; set; } = new();

        /// <summary>母体：当前正在孕育的胎儿预计分娩日(游戏天)。&lt;=0 表示未怀孕。</summary>
        public double PregnancyDueDay { get; set; } = -1.0;

        /// <summary>母体：胎儿父亲实体 Id(用于分娩时设置幼崽的 FatherId)。0 表示未知。</summary>
        public int PregnancyFatherId { get; set; }

        /// <summary>母体：胎儿父亲模板名(用于近亲检测与记录)。</summary>
        public string PregnancyFatherTemplate { get; set; }

        /// <summary>上次成功分娩时刻(游戏天)。用于怀孕冷却(PregnancyCooldownDays)。</summary>
        public double LastBirthDay { get; set; } = -1.0;

        /// <summary>上次繁殖周期检查时刻(游戏天)。用于节流，避免每帧都跑密度/温湿度检测。</summary>
        public double LastBreedingCheckDay { get; set; } = -1.0;

        /// <summary>上次幼崽每日存活判定的整数天。避免同一天重复判定。</summary>
        public long LastCubSurvivalDay { get; set; } = -1L;

        /// <summary>幼崽出生时的 BoxSize 缓存，便于进阶到成年时恢复。</summary>
        [JsonIgnore]
        public Engine.Vector3? SavedCubBoxSize { get; set; }

        /// <summary>是否已应用过碰撞盒调整(避免重复调整)。</summary>
        [JsonIgnore]
        public bool BoxSizeApplied { get; set; }

        // ==================== 派生查询 ====================

        /// <summary>是否处于繁殖季节(由外部 SubsystemBreeding 用当前 Season 比对 SpeciesConfig.ParsedSeasons)。</summary>
        [JsonIgnore]
        public bool IsInEstrus { get; set; }

        /// <summary>是否为成年个体。</summary>
        [JsonIgnore]
        public bool IsAdult => Stage == GrowthStage.Adult;

        /// <summary>是否可交配(成年 + 母体未怀孕 + 在繁殖季 + 度过冷却)。</summary>
        public bool CanMate(double currentDay, float cooldownDays)
        {
            if (!IsAdult) return false;
            if (Gender == BreedingGender.Female && PregnancyDueDay > 0.0) return false;
            if (LastBirthDay > 0.0 && currentDay - LastBirthDay < cooldownDays) return false;
            return true;
        }
        
        // ==================== 渲染辅助查询 ====================

        /// <summary>成长进度(0~1)。幼崽期：0~1 表示从出生到成年的进度；成年期恒为 1。</summary>
        public float GetGrowthProgress(double currentDay, float cubDurationDays)
        {
            if (IsAdult) return 1f;
            if (cubDurationDays <= 0f) return 1f;
            double age = currentDay - BirthDay;
            return Math.Clamp((float)(age / cubDurationDays), 0f, 1f);
        }

        /// <summary>成长阶段的中文显示名。</summary>
        public string GetStageDisplayName()
        {
            return Stage == GrowthStage.Cub ? "幼崽期" : "成年期";
        }

        /// <summary>性别的中文显示名。</summary>
        public string GetGenderDisplayName()
        {
            return Gender == BreedingGender.Male ? "♂公" : "♀母";
        }

        /// <summary>繁殖状态简述(用于渲染显示)。例如："发情中" / "怀孕中" / "可交配" / "未在季"。</summary>
        public string GetBreedingStatus(double currentDay)
        {
            if (Gender == BreedingGender.Female && PregnancyDueDay > 0.0)
            {
                double remain = PregnancyDueDay - currentDay;
                return remain > 0 ? $"怀孕中({remain:F1}天)" : "即将分娩";
            }
            if (IsInEstrus) return "发情中";
            if (IsAdult) return "未在季";
            return "成长中";
        }

        // ==================== JSON 持久化 ====================

        /// <summary>序列化为 JSON 字符串(写入 SpawnEntityData.Data)。</summary>
        public string Serialize()
        {
            try
            {
                return JsonSerializer.Serialize(this);
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 状态序列化失败: " + e.Message);
                return string.Empty;
            }
        }

        /// <summary>从 SpawnEntityData.Data 反序列化。失败返回 null。</summary>
        public static BreedingState Deserialize(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;
            try
            {
                JsonSerializerOptions opts = new() { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<BreedingState>(data, opts);
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 状态反序列化失败: " + e.Message);
                return null;
            }
        }

        /// <summary>记录一次成功交配对象，保留最近 N 个(FIFO)。</summary>
        public void RecordMate(int mateEntityId, int limit)
        {
            if (limit <= 0) return;
            while (RecentMates.Count >= limit)
            {
                // 移除最旧的一个(队首)
                RecentMates.RemoveAt(0);
            }
            RecentMates.Add(mateEntityId);
        }

        /// <summary>当前对象是否在最近 N 次交配名单中。</summary>
        public bool IsRecentMate(int mateEntityId)
        {
            return RecentMates.Contains(mateEntityId);
        }
    }
}
