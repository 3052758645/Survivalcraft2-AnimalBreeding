using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统配置。对应 MOD/Assets/BreedingConfig.json。
    /// 全局只保留总开关 Enabled，其余所有参数都按物种独立配置(Species)。
    /// 每个物种(Wolf_Gray 等)可自定义：孕期/体型/攻击力/交配半径/虚弱期等。
    /// </summary>
    public class BreedingConfig
    {
        /// <summary>全局总开关。false 时繁殖系统完全不生效。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>按实体模板名索引的物种配置。每个物种独立设置所有繁殖参数。</summary>
        public Dictionary<string, SpeciesConfig> Species { get; set; } = new();

        // ==================== 加载与缓存 ====================

        public static BreedingConfig Current { get; private set; }

        public static BreedingConfig Load()
        {
            try
            {
                string json = ContentManager.Get<string>("BreedingConfig", ".json");
                if (string.IsNullOrEmpty(json))
                {
                    Log.Warning("[Breeding] BreedingConfig.json 内容为空，繁殖系统将禁用");
                    Current = new BreedingConfig { Enabled = false };
                    return Current;
                }
                JsonSerializerOptions opts = new()
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };
                BreedingConfig cfg = JsonSerializer.Deserialize<BreedingConfig>(json, opts) ?? new BreedingConfig();
                cfg.Species ??= new Dictionary<string, SpeciesConfig>();
                foreach (KeyValuePair<string, SpeciesConfig> kv in cfg.Species)
                {
                    kv.Value?.Normalize();
                }
                Current = cfg;
                Log.Information($"[Breeding] 配置加载完成，物种数={cfg.Species.Count}，Enabled={cfg.Enabled}");
                return Current;
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 配置加载失败: " + e.Message);
                Current = new BreedingConfig { Enabled = false };
                return Current;
            }
        }

        public SpeciesConfig GetSpecies(string templateName)
        {
            if (string.IsNullOrEmpty(templateName)) return null;
            return Species.TryGetValue(templateName, out SpeciesConfig s) ? s : null;
        }
    }

    /// <summary>
    /// 单物种繁殖配置。所有繁殖参数都按物种独立设置。
    /// 这样不同生物可以有不同的孕期/体型/攻击力/交配半径等。
    /// </summary>
    public class SpeciesConfig
    {
        // ==================== 繁殖季节与成长 ====================

        /// <summary>1~2 个繁殖季节。可选: Summer / Autumn / Winter / Spring。</summary>
        public List<string> BreedingSeasons { get; set; } = new();

        /// <summary>幼崽期持续天数(游戏天)。到期后进阶成年。</summary>
        public float CubDurationDays { get; set; } = 3f;

        // ==================== 时间参数(现实秒) ====================

        /// <summary>孕期持续秒数。母体交配成功后此秒数分娩。</summary>
        public float GestationSeconds { get; set; } = 30.0f;

        /// <summary>交配所需相处时间。公母在 MateRadius 内持续相处此秒数后触发交配。</summary>
        public float MatingRequiredProximitySeconds { get; set; } = 10.0f;

        /// <summary>虚弱期持续秒数。交配后仅公体虚弱，分娩后母体虚弱。虚弱期间不发情。</summary>
        public float WeaknessSeconds { get; set; } = 60.0f;

        /// <summary>公体竞争时追击竞争对手的时长(现实秒)。</summary>
        public float RivalChaseTime { get; set; } = 30.0f;

        // ==================== 距离参数(方块) ====================

        /// <summary>交配判定半径。公母在此距离内持续相处才算交配。</summary>
        public float MateRadius { get; set; } = 2.0f;

        /// <summary>公体寻找母体的搜索半径。公体发情时在此范围内寻找母体并走过去。</summary>
        public float SeekRadius { get; set; } = 20.0f;

        /// <summary>分娩时幼崽在母体附近的随机偏移范围(方块)。</summary>
        public float BirthSpawnOffset { get; set; } = 1.5f;

        // ==================== 攻击力参数 ====================

        /// <summary>幼崽攻击力系数(与成年基准相乘)。</summary>
        public float CubAttackFactor { get; set; } = 0.3f;

        /// <summary>成年攻击力系数(基准1.0)。</summary>
        public float AdultAttackFactor { get; set; } = 1.0f;

        /// <summary>公体攻击力额外倍率(母体为1.0)。公=Adult×MaleBonus，母=Adult×1.0。</summary>
        public float MaleAttackBonus { get; set; } = 1.3f;

        // ==================== 体型参数 ====================

        /// <summary>幼崽出生时的体型缩放(相对原版模板 BoxSize/ModelScale)。</summary>
        public float CubBoxScale { get; set; } = 0.5f;

        /// <summary>成年公体体型缩放(相对原版)。</summary>
        public float AdultMaleBoxScale { get; set; } = 1.3f;

        /// <summary>成年母体体型缩放(相对原版)。</summary>
        public float AdultFemaleBoxScale { get; set; } = 1.0f;

        // ==================== 仇恨与性别参数 ====================

        /// <summary>发情期仇恨范围倍率(乘到 ChaseRange factor 上)。</summary>
        public float EstrusChaseRangeMultiplier { get; set; } = 2.0f;

        /// <summary>幼崽/自然生成个体的公体概率(0~1)。</summary>
        public float CubMaleProbability { get; set; } = 0.5f;

        // ==================== 运行时(不序列化) ====================

        [JsonIgnore]
        public HashSet<Season> ParsedSeasons { get; private set; } = new();

        public void Normalize()
        {
            BreedingSeasons ??= new List<string>();
            ParsedSeasons = new HashSet<Season>();
            foreach (string s in BreedingSeasons)
            {
                if (Enum.TryParse(s, ignoreCase: true, out Season season))
                {
                    ParsedSeasons.Add(season);
                }
                else
                {
                    Log.Warning($"[Breeding] 未知季节字符串: {s}，已忽略");
                }
            }
            if (CubDurationDays <= 0f) CubDurationDays = 3f;
            if (GestationSeconds <= 0f) GestationSeconds = 30f;
            if (MatingRequiredProximitySeconds <= 0f) MatingRequiredProximitySeconds = 10f;
            if (WeaknessSeconds < 0f) WeaknessSeconds = 60f;
            if (MateRadius <= 0f) MateRadius = 2f;
            if (SeekRadius <= 0f) SeekRadius = 20f;
        }
    }
}
