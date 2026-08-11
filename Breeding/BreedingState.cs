using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameEntitySystem;
using Engine;
using Game;

namespace Game
{
    /// <summary>生物性别。公=体型更大、攻击力更高；母=可怀孕分娩。</summary>
    public enum BreedingGender
    {
        Male = 0,
        Female = 1
    }

    /// <summary>成长阶段。幼崽期 → 成年期。</summary>
    public enum GrowthStage
    {
        Cub = 0,
        Adult = 1
    }

    /// <summary>
    /// 单只动物的繁殖运行时状态(简化版)。
    /// 不挂在实体上，由 SubsystemBreeding 用 Dictionary 缓存。
    /// 持久化通过 SpawnEntityData.Data(JSON)走 OnSaveSpawnData / OnReadSpawnData。
    ///
    /// 机制：
    /// · 成长度 0~1，幼崽期从 0 线性增长到 1，成年后恒为 1。
    /// · 体型随成长度从 CubBoxScale 插值到成年尺寸(公 AdultMaleBoxScale / 母 AdultFemaleBoxScale)。
    /// · 母体怀孕用 PregnancyRemainingSeconds(现实秒倒计时)，到期分娩。
    /// </summary>
    public class BreedingState
    {
        /// <summary>该状态所属实体的模板名(如 Wolf_Gray)。</summary>
        public string TemplateName { get; set; }

        /// <summary>性别。</summary>
        public BreedingGender Gender { get; set; }

        /// <summary>成长阶段。</summary>
        public GrowthStage Stage { get; set; } = GrowthStage.Cub;

        /// <summary>出生时刻(游戏天，SubsystemTimeOfDay.Day)。</summary>
        public double BirthDay { get; set; }

        /// <summary>母体：孕期剩余秒数(现实秒)。<=0 表示未怀孕。交配成功时设为 GestationSeconds。</summary>
        public float PregnancyRemainingSeconds { get; set; } = -1f;

        /// <summary>母体：胎儿父亲实体 Id(仅记录)。</summary>
        public int PregnancyFatherId { get; set; }

        // ==================== 运行时(不序列化) ====================

        /// <summary>是否处于繁殖季节(由 SubsystemBreeding 每帧设置)。</summary>
        [JsonIgnore]
        public bool IsInEstrus { get; set; }

        /// <summary>是否为成年个体。</summary>
        [JsonIgnore]
        public bool IsAdult => Stage == GrowthStage.Adult;

        /// <summary>原版模板 BoxSize 缓存(第一次应用体型时保存，用于按成长度缩放)。</summary>
        [JsonIgnore]
        public Vector3? OriginalBoxSize { get; set; }

        // ==================== 派生查询 ====================

        /// <summary>成长进度(0~1)。幼崽期：从出生到成年的线性进度；成年恒为 1。</summary>
        public float GetGrowthProgress(double currentDay, float cubDurationDays)
        {
            if (IsAdult) return 1f;
            if (cubDurationDays <= 0f) return 1f;
            double age = currentDay - BirthDay;
            return Math.Clamp((float)(age / cubDurationDays), 0f, 1f);
        }

        /// <summary>成长阶段中文显示名。</summary>
        public string GetStageDisplayName()
        {
            return Stage == GrowthStage.Cub ? "幼崽期" : "成年期";
        }

        /// <summary>性别中文显示名。</summary>
        public string GetGenderDisplayName()
        {
            return Gender == BreedingGender.Male ? "♂公" : "♀母";
        }

        /// <summary>繁殖状态简述(渲染显示用)。</summary>
        public string GetBreedingStatus()
        {
            if (Gender == BreedingGender.Female && PregnancyRemainingSeconds > 0f)
            {
                return $"怀孕中({PregnancyRemainingSeconds:F0}秒)";
            }
            if (IsInEstrus) return "发情中";
            if (IsAdult) return "未在季";
            return "成长中";
        }

        // ==================== JSON 持久化 ====================

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
    }
}
