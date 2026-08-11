using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统配置(简化版)。对应 MOD/Assets/BreedingConfig.json。
    /// 机制：发情期公狼主动寻找母狼 → 相处N秒交配 → 双方虚弱期 → 母狼孕期 → 分娩 → 母狼虚弱期。
    /// 体型(BoxSize+ModelScale)随成长度从 CubBoxScale 线性插值到成年尺寸(公大母小)。
    /// 攻击力：幼崽×CubAttackFactor / 成年×AdultAttackFactor / 公狼额外×MaleAttackBonus。
    /// 仇恨：幼崽/怀孕母狼不产生仇恨；公狼正常攻击玩家。
    /// </summary>
    public class BreedingConfig
    {
        /// <summary>全局开关。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>孕期持续秒数(现实秒)。母狼交配成功后此秒数分娩。</summary>
        public float GestationSeconds { get; set; } = 30.0f;

        /// <summary>发情期仇恨范围倍率(乘到 ChaseRange factor 上)。</summary>
        public float EstrusChaseRangeMultiplier { get; set; } = 2.0f;

        /// <summary>幼崽攻击力系数(与成年基准相乘)。</summary>
        public float CubAttackFactor { get; set; } = 0.3f;

        /// <summary>成年攻击力系数(基准1.0)。</summary>
        public float AdultAttackFactor { get; set; } = 1.0f;

        /// <summary>公狼攻击力额外倍率(母狼为1.0)。公=Adult×MaleBonus，母=Adult×1.0。</summary>
        public float MaleAttackBonus { get; set; } = 1.3f;

        /// <summary>幼崽出生时的体型缩放(相对原版模板 BoxSize/ModelScale)。</summary>
        public float CubBoxScale { get; set; } = 0.5f;

        /// <summary>成年公狼体型缩放(相对原版)。</summary>
        public float AdultMaleBoxScale { get; set; } = 1.3f;

        /// <summary>成年母狼体型缩放(相对原版)。</summary>
        public float AdultFemaleBoxScale { get; set; } = 1.0f;

        /// <summary>交配判定半径(方块)。公母在此距离内持续相处才算交配。</summary>
        public float MateRadius { get; set; } = 2.0f;

        /// <summary>公狼寻找母狼的搜索半径(方块)。公狼发情时在此范围内寻找母狼并走过去。</summary>
        public float SeekRadius { get; set; } = 20.0f;

        /// <summary>交配所需相处时间(现实秒)。公母在 MateRadius 内持续相处此秒数后触发交配。</summary>
        public float MatingRequiredProximitySeconds { get; set; } = 10.0f;

        /// <summary>虚弱期持续秒数(现实秒)。交配/分娩后进入虚弱期，期间不处于发情状态。</summary>
        public float WeaknessSeconds { get; set; } = 60.0f;

        /// <summary>公狼竞争时追击竞争对手的时长(现实秒)。</summary>
        public float RivalChaseTime { get; set; } = 30.0f;

        /// <summary>分娩时幼崽在母体附近的随机偏移范围(方块)。</summary>
        public float BirthSpawnOffset { get; set; } = 1.5f;

        /// <summary>幼崽/自然生成个体的公狼概率(0~1)。</summary>
        public float CubMaleProbability { get; set; } = 0.5f;

        /// <summary>按实体模板名索引的物种配置。</summary>
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
                Log.Information($"[Breeding] 配置加载完成，物种数={cfg.Species.Count}，Enabled={cfg.Enabled}，GestationSeconds={cfg.GestationSeconds}，MateRadius={cfg.MateRadius}，SeekRadius={cfg.SeekRadius}，MatingProximity={cfg.MatingRequiredProximitySeconds}，Weakness={cfg.WeaknessSeconds}");
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

    /// <summary>单物种繁殖配置(简化版)。</summary>
    public class SpeciesConfig
    {
        /// <summary>1~2 个繁殖季节。可选: Summer / Autumn / Winter / Spring。</summary>
        public List<string> BreedingSeasons { get; set; } = new();

        /// <summary>幼崽期持续天数(游戏天)。到期后进阶成年。</summary>
        public float CubDurationDays { get; set; } = 3f;

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
        }
    }
}
