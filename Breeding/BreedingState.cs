using System;
using System.Text;
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
    /// · 体型(BoxSize+ModelScale)随成长度从 CubBoxScale 插值到成年尺寸。
    /// · 母体怀孕用 PregnancyRemainingSeconds(现实秒倒计时)，到期分娩。
    /// · 公母交配需相处 MatingRequiredProximitySeconds 秒，交配后双方进入虚弱期。
    /// · 虚弱期不处于发情状态；分娩后母体也进入虚弱期。
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

        /// <summary>母体：与公狼相处计时(现实秒)。公狼在 MateRadius 内时累加，达到阈值触发交配。</summary>
        public float MatingProximitySeconds { get; set; }

        /// <summary>虚弱期剩余秒数(现实秒)。<=0 表示非虚弱。交配/分娩后设为 WeaknessSeconds。</summary>
        public float WeaknessRemainingSeconds { get; set; } = -1f;

        /// <summary>
        /// 已喂食状态剩余秒数(现实秒)。<=0 表示未喂食。
        /// 玩家喂食 FeedItem 指定物品后设为 FedDurationSeconds，期间(配合季节)可发情交配。
        /// 仅当物种 RequireFeeding=true 时此项才有意义。
        /// </summary>
        public float FedRemainingSeconds { get; set; } = -1f;

        // ==================== 运行时(不序列化) ====================

        /// <summary>是否处于发情期(由 SubsystemBreeding 每帧设置：在繁殖季节且不在虚弱期)。</summary>
        [JsonIgnore]
        public bool IsInEstrus { get; set; }

        /// <summary>是否为成年个体。</summary>
        [JsonIgnore]
        public bool IsAdult => Stage == GrowthStage.Adult;

        /// <summary>是否处于虚弱期。</summary>
        [JsonIgnore]
        public bool IsWeak => WeaknessRemainingSeconds > 0f;

        /// <summary>是否已喂食(喂食发情条件满足)。仅 RequireFeeding=true 的物种会检查此项。</summary>
        [JsonIgnore]
        public bool IsFed => FedRemainingSeconds > 0f;

        /// <summary>原版模板 BoxSize 缓存(第一次应用体型时保存，用于按成长度缩放)。</summary>
        [JsonIgnore]
        public Vector3? OriginalBoxSize { get; set; }

        /// <summary>原版模板 ModelScale 缓存(第一次应用体型时保存，用于按成长度缩放视觉模型)。</summary>
        [JsonIgnore]
        public float? OriginalModelScale { get; set; }

        /// <summary>公狼当前追求的母狼实体 Id(0=无目标)。用于检测多公追同一母狼的竞争。</summary>
        [JsonIgnore]
        public int TargetFemaleId { get; set; }

        // ==================== 派生查询 ====================

        /// <summary>成长进度(0~1)。幼崽期：从出生到成年的线性进度；成年恒为 1。</summary>
        public float GetGrowthProgress(double currentDay, float cubDurationDays)
        {
            if (IsAdult) return 1f;
            if (cubDurationDays <= 0f) return 1f;
            double age = currentDay - BirthDay;
            return Math.Clamp((float)(age / cubDurationDays), 0f, 1f);
        }

        /// <summary>成长阶段显示名(走国际化)。</summary>
        public string GetStageDisplayName()
        {
            return Stage == GrowthStage.Cub
                ? LanguageControl.Get("BreedingMod", "Stage", "Cub")
                : LanguageControl.Get("BreedingMod", "Stage", "Adult");
        }

        /// <summary>性别显示名(走国际化)。</summary>
        public string GetGenderDisplayName()
        {
            return Gender == BreedingGender.Male
                ? LanguageControl.Get("BreedingMod", "Gender", "Male")
                : LanguageControl.Get("BreedingMod", "Gender", "Female");
        }

        /// <summary>繁殖状态简述(渲染显示用，走国际化)。怀孕优先显示，其次虚弱，最后发情/喂食。</summary>
        public string GetBreedingStatus(SpeciesConfig species = null)
        {
            // 母体怀孕优先显示(即使同时处于虚弱期)
            if (Gender == BreedingGender.Female && PregnancyRemainingSeconds > 0f)
            {
                return string.Format(LanguageControl.Get("BreedingMod", "Status", "Pregnant"), PregnancyRemainingSeconds.ToString("F0"));
            }
            if (IsWeak)
            {
                return string.Format(LanguageControl.Get("BreedingMod", "Status", "Weak"), WeaknessRemainingSeconds.ToString("F0"));
            }
            if (IsInEstrus)
            {
                if (Gender == BreedingGender.Female && MatingProximitySeconds > 0f)
                {
                    return string.Format(LanguageControl.Get("BreedingMod", "Status", "EstrusMating"), MatingProximitySeconds.ToString("F0"));
                }
                return LanguageControl.Get("BreedingMod", "Status", "Estrus");
            }
            // 条件性繁衍：在季节内但未喂食 → 提示需喂食
            if (species != null && species.RequireFeeding && IsAdult && !IsFed)
            {
                return LanguageControl.Get("BreedingMod", "Status", "NeedFeeding");
            }
            if (IsAdult) return LanguageControl.Get("BreedingMod", "Status", "NotInSeason");
            return LanguageControl.Get("BreedingMod", "Status", "Growing");
        }

        // ==================== JSON 持久化 ====================

        public string Serialize()
        {
            try
            {
                string json = JsonSerializer.Serialize(this);
                // 用 Base64 编码避免 JSON 中的逗号破坏 SpawnEntityData 的逗号分隔格式
                // (SubsystemSpawn.LoadSpawnsData/SaveSpawnsData 用 ',' 分隔字段)
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                return Convert.ToBase64String(bytes);
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
                string json;
                // 优先尝试 Base64 解码(新格式)
                try
                {
                    byte[] bytes = Convert.FromBase64String(data);
                    json = Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    // 兼容旧格式(直接 JSON，可能因逗号被截断而无效)
                    json = data;
                }
                JsonSerializerOptions opts = new() { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<BreedingState>(json, opts);
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 状态反序列化失败: " + e.Message);
                return null;
            }
        }
    }
}
