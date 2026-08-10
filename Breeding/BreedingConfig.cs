using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine;
using Game;

namespace HYKJ.Breeding
{
    /// <summary>
    /// 动物繁殖系统的配置入口。对应 MOD/Assets/BreedingConfig.json。
    /// 所有属性以配置文件方式存在：全局开关、阈值、每个物种的繁殖季节/最适温湿度/幼崽模板/窝方块等。
    /// </summary>
    public class BreedingConfig
    {
        /// <summary>全局开关。false 时整个繁殖系统不生效。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>母体成功分娩后的再次怀孕冷却(游戏天)。</summary>
        public float PregnancyCooldownDays { get; set; } = 1.0f;

        /// <summary>默认怀孕成功率(0~1)。</summary>
        public float DefaultPregnancySuccessRate { get; set; } = 0.7f;

        /// <summary>母体血量低于此比例(0~1) → 怀孕失败。</summary>
        public float LowHealthThreshold { get; set; } = 0.4f;

        /// <summary>残血攻击力修正触发线。当前 Health &lt; 此值 → 攻击力 ×0.5。</summary>
        public float LowHealthAttackThreshold { get; set; } = 0.3f;

        /// <summary>温度归一化(0~1)后与最适值差值阈值。</summary>
        public float TemperatureDeviationThreshold { get; set; } = 0.4f;

        /// <summary>湿度归一化(0~1)后与最适值差值阈值。</summary>
        public float HumidityDeviationThreshold { get; set; } = 0.4f;

        /// <summary>密度检测半径(方块)。</summary>
        public int DensityRadius { get; set; } = 15;

        /// <summary>密度检测同类成年上限。</summary>
        public int DensityMaxAdults { get; set; } = 8;

        /// <summary>幼崽存活判定半径(方块)。</summary>
        public int CubSurvivalCheckRadius { get; set; } = 10;

        /// <summary>幼崽每天夭折概率(0~1)。</summary>
        public float CubDailyDeathProbability { get; set; } = 0.3f;

        /// <summary>幼崽攻击力系数。</summary>
        public float CubAttackFactor { get; set; } = 0.3f;

        /// <summary>成年攻击力系数。</summary>
        public float AdultAttackFactor { get; set; } = 1.0f;

        /// <summary>发情期攻击力系数(与成长系数相乘)。</summary>
        public float EstrusAttackFactor { get; set; } = 0.5f;

        /// <summary>残血攻击力系数(与成长系数+发情系数相乘)。</summary>
        public float LowHealthAttackFactor { get; set; } = 0.5f;

        /// <summary>发情期仇恨范围倍率(乘到 ChaseRange factor 上)。</summary>
        public float EstrusChaseRangeMultiplier { get; set; } = 2.0f;

        /// <summary>是否启用近亲检测。</summary>
        public bool InbreedingEnabled { get; set; } = true;

        /// <summary>重复配对检测：记录最近 N 次成功交配对象。</summary>
        public int RecentMatesLimit { get; set; } = 3;

        /// <summary>按实体模板名索引的物种繁殖属性。</summary>
        public Dictionary<string, SpeciesConfig> Species { get; set; } = new();

        // ==================== 加载与缓存 ====================

        /// <summary>当前生效的配置(加载后缓存)。</summary>
        public static BreedingConfig Current { get; private set; }

        /// <summary>从 MOD 资源路径加载配置。失败时回退为空配置(Enabled=false)。
        /// 注：mod pak 内 Assets/ 下的文件被注册到 ContentManager 时会去掉 "Assets/" 前缀，
        /// 因此这里查询的 key 是 "BreedingConfig" + ".json"，不带 Assets/ 前缀。
        /// </summary>
        public static BreedingConfig Load()
        {
            try
            {
                string json = ContentManager.Get<string>("BreedingConfig", ".json");
                if (string.IsNullOrEmpty(json))
                {
                    Log.Warning("[HYKJ.Breeding] BreedingConfig.json 内容为空，繁殖系统将禁用");
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
                // 兜底：缺字段时给默认值
                cfg.Species ??= new Dictionary<string, SpeciesConfig>();
                foreach (KeyValuePair<string, SpeciesConfig> kv in cfg.Species)
                {
                    kv.Value?.Normalize();
                }
                Current = cfg;
                Log.Information($"[HYKJ.Breeding] 配置加载完成，物种数={cfg.Species.Count}，Enabled={cfg.Enabled}");
                return Current;
            }
            catch (Exception e)
            {
                Log.Warning("[HYKJ.Breeding] 配置加载失败: " + e.Message);
                Current = new BreedingConfig { Enabled = false };
                return Current;
            }
        }

        /// <summary>按实体模板名取物种配置；不存在则返回 null(该物种不参与繁殖)。</summary>
        public SpeciesConfig GetSpecies(string templateName)
        {
            if (string.IsNullOrEmpty(templateName)) return null;
            return Species.TryGetValue(templateName, out SpeciesConfig s) ? s : null;
        }
    }

    /// <summary>
    /// 单个物种的繁殖属性。每个字段对应配置文件 Species[templateName] 下的一项。
    /// </summary>
    public class SpeciesConfig
    {
        /// <summary>1~2 个繁殖季节。可选值: Summer / Autumn / Winter / Spring。不在季节内 → 无法触发交配。</summary>
        public List<string> BreedingSeasons { get; set; } = new();

        /// <summary>物种最适温度(0~1 归一化)。</summary>
        public float OptimalTemperature { get; set; } = 0.5f;

        /// <summary>物种最适湿度(0~1 归一化)。</summary>
        public float OptimalHumidity { get; set; } = 0.5f;

        /// <summary>幼崽期持续天数(游戏天)。</summary>
        public float CubDurationDays { get; set; } = 3f;

        /// <summary>幼崽模板名。若该模板在数据库中不存在，则降级为父模板(同模型)但缩小碰撞盒。</summary>
        public string CubTemplate { get; set; }

        /// <summary>幼崽碰撞盒大小(X,Y,Z)。null 表示不调整。</summary>
        public List<float> CubBoxSize { get; set; }

        /// <summary>成年碰撞盒大小(X,Y,Z)。null 表示不调整。</summary>
        public List<float> AdultBoxSize { get; set; }

        /// <summary>窝/食物源方块名(BlocksManager.Blocks 的索引键)。幼崽周围无这些方块 → 触发夭折判定。</summary>
        public List<string> NestBlocks { get; set; } = new();

        /// <summary>缓存解析后的 Season 枚举集合，避免每次交配时再解析字符串。</summary>
        [JsonIgnore]
        public HashSet<Season> ParsedSeasons { get; private set; } = new();

        /// <summary>缓存 NestBlocks 对应的方块索引(运行时由 SubsystemBreeding 初始化)。</summary>
        [JsonIgnore]
        public HashSet<int> NestBlockIndices { get; set; } = new();

        /// <summary>配置加载后做归一化：解析季节字符串、给缺省值。</summary>
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
                    Log.Warning($"[HYKJ.Breeding] 未知季节字符串: {s}，已忽略");
                }
            }
            if (CubDurationDays <= 0f) CubDurationDays = 3f;
            NestBlocks ??= new List<string>();
        }
    }
}
