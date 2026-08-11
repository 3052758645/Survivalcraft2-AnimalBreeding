using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统配置。对应 MOD/Assets/BreedingConfig.json(主配置)。
    /// 全局只保留总开关 Enabled，其余所有参数都按物种独立配置(Species)。
    /// 每个物种(Wolf_Gray 等)可自定义：孕期/体型/攻击力/交配半径/虚弱期等。
    ///
    /// 多源配置合并(方案B)：
    /// · 主配置 BreedingConfig.json — 决定 Enabled 总开关 + 自带物种
    /// · 扩展配置 BreedingConfig.{ModId}.json — 第三方模组自带，仅追加 Species
    /// · 同名模板：主配置优先；扩展之间按文件名排序，先到先得
    /// · 扩展配置中的 Enabled 字段被忽略(防止第三方关闭整个系统)
    /// </summary>
    public class BreedingConfig
    {
        /// <summary>全局总开关。false 时繁殖系统完全不生效。仅主配置 BreedingConfig.json 的值生效。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>按实体模板名索引的物种配置。每个物种独立设置所有繁殖参数。</summary>
        public Dictionary<string, SpeciesConfig> Species { get; set; } = new();

        // ==================== 加载与缓存 ====================

        public static BreedingConfig Current { get; private set; }

        /// <summary>
        /// 加载并合并所有 BreedingConfig*.json。
        /// 1) 先加载主配置 BreedingConfig.json(决定 Enabled + 主物种)
        /// 2) 再按文件名排序加载扩展配置 BreedingConfig.{ModId}.json(仅追加 Species)
        /// 同名模板主配置永远优先；扩展之间先到先得，冲突打 Warning 跳过。
        /// </summary>
        public static BreedingConfig Load()
        {
            try
            {
                JsonSerializerOptions opts = new()
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };

                // 1) 主配置 BreedingConfig.json — 决定 Enabled
                string mainJson = ContentManager.Get<string>("BreedingConfig", ".json");
                BreedingConfig cfg;
                if (string.IsNullOrEmpty(mainJson))
                {
                    Log.Warning("[Breeding] 主配置 BreedingConfig.json 内容为空，繁殖系统将禁用");
                    cfg = new BreedingConfig { Enabled = false };
                }
                else
                {
                    cfg = JsonSerializer.Deserialize<BreedingConfig>(mainJson, opts) ?? new BreedingConfig();
                }
                cfg.Species ??= new Dictionary<string, SpeciesConfig>();
                foreach (KeyValuePair<string, SpeciesConfig> kv in cfg.Species)
                {
                    kv.Value?.Normalize();
                    kv.Value?.SetSpeciesName(kv.Key);
                }
                Log.Information($"[Breeding] 主配置加载完成，物种数={cfg.Species.Count}，Enabled={cfg.Enabled}");

                // 2) 扩展配置 BreedingConfig.{ModId}.json — 仅追加 Species
                List<ContentInfo> extensions = ListExtensionConfigs();
                Log.Information($"[Breeding] 发现 {extensions.Count} 个扩展配置文件");
                foreach (ContentInfo ext in extensions)
                {
                    MergeExtension(cfg, ext, opts);
                }

                Current = cfg;
                Log.Information($"[Breeding] 全部配置合并完成，总物种数={cfg.Species.Count}，Enabled={cfg.Enabled}");
                return Current;
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 配置加载失败: " + e.Message);
                Current = new BreedingConfig { Enabled = false };
                return Current;
            }
        }

        /// <summary>
        /// 列出所有扩展配置文件(BreedingConfig.{ModId}.json)。
        /// 主配置 BreedingConfig.json 被排除。按 Filename 排序，保证合并顺序稳定。
        /// </summary>
        static List<ContentInfo> ListExtensionConfigs()
        {
            List<ContentInfo> result = new();
            foreach (ContentInfo info in ContentManager.List())
            {
                if (info == null || info.Filename == null) continue;
                // 必须以 .json 结尾
                if (!info.Filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                // 文件名去后缀后必须等于 BreedingConfig 或 BreedingConfig.{ModId}
                string stem = info.Filename.Substring(0, info.Filename.Length - ".json".Length);
                if (stem.Equals("BreedingConfig", StringComparison.OrdinalIgnoreCase)) continue; // 主配置跳过
                if (!stem.StartsWith("BreedingConfig.", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(info);
            }
            result.Sort((a, b) => string.Compare(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// 合并单个扩展配置到主配置。
        /// · 扩展配置的 Enabled 被忽略(仅主配置可控制总开关)
        /// · Species 同名模板：主配置已有则跳过并 Warning，否则追加
        /// 用 ContentManager.Get<string> 读取(走标准 IContentReader 流程，比 Duplicate() 更可靠)。
        /// </summary>
        static void MergeExtension(BreedingConfig main, ContentInfo extInfo, JsonSerializerOptions opts)
        {
            try
            {
                // 用 Get<string> 读取，throwOnNotFound=false 避免抛异常
                string json = ContentManager.Get<string>(extInfo.ContentPath, extInfo.ContentSuffix, false);
                if (string.IsNullOrEmpty(json))
                {
                    Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 内容为空或读取失败，跳过");
                    return;
                }
                BreedingConfig ext = JsonSerializer.Deserialize<BreedingConfig>(json, opts);
                if (ext?.Species == null || ext.Species.Count == 0)
                {
                    Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 无 Species 条目，跳过");
                    return;
                }
                int added = 0, skipped = 0;
                foreach (KeyValuePair<string, SpeciesConfig> kv in ext.Species)
                {
                    if (kv.Value == null) continue;
                    if (main.Species.ContainsKey(kv.Key))
                    {
                        Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 的物种 '{kv.Key}' 与主配置/先加载的扩展冲突，跳过");
                        skipped++;
                        continue;
                    }
                    kv.Value.Normalize();
                    kv.Value.SetSpeciesName(kv.Key);
                    main.Species[kv.Key] = kv.Value;
                    added++;
                }
                Log.Information($"[Breeding] 扩展配置 {extInfo.Filename} 合并完成：新增 {added} 个物种，跳过 {skipped} 个冲突");
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 解析失败: {e.Message}");
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

        // ==================== 交互拦截(繁殖期/幼崽期禁止上鞍骑乘) ====================

        /// <summary>
        /// 繁殖期间(发情/怀孕/虚弱)是否禁止交互(上鞍+骑乘)。默认 true。
        /// 仅对可上鞍/可骑乘物种(Horse/Donkey/Camel/Reindeer/Ostrich)有意义。
        /// </summary>
        public bool BlockInteractDuringBreeding { get; set; } = true;

        /// <summary>
        /// 幼崽期是否禁止交互(上鞍+骑乘)。默认 true。
        /// 仅对可上鞍/可骑乘物种(Horse/Donkey/Camel/Reindeer/Ostrich)有意义。
        /// </summary>
        public bool BlockInteractDuringCub { get; set; } = true;

        /// <summary>
        /// 上鞍被拦截时是否仍消耗玩家手中的鞍。默认 false(不消耗，鞍退回)。
        /// true = 鞍被扣掉但马没上鞍(作为惩罚，玩家会看到"该生物无法上鞍"提示)。
        /// false = 鞍退回玩家背包，相当于上鞍操作完全取消。
        /// 注:原版 OnUse 在调用我们的 hook 之前不会扣鞍，所以此选项可控。
        /// </summary>
        public bool ConsumeSaddleOnBlocked { get; set; } = false;

        // ==================== 物种别名与幼崽模板 ====================

        /// <summary>
        /// 物种别名列表。当前模板可与此列表中的模板互相交配。
        /// 例: Cow 配 Aliases=["Bull"]，则 Cow(母)可和 Bull(公)交配；
        /// 反之 Bull 也需配 Aliases=["Cow"] 才能双向识别。幼崽模板由各自 CubTemplateOverride 决定。
        /// </summary>
        public List<string> Aliases { get; set; } = new();

        /// <summary>
        /// 幼崽生成时使用的模板名。空或 null = 沿用母体模板(默认)。
        /// 例: Cow 配 CubTemplateOverride="Cow" 可保证母牛只生小母牛(Cow 模板)，不会生 Bull；
        /// 不配则母牛生母牛、母公牛生公牛(沿用母体)。
        /// </summary>
        public string CubTemplateOverride { get; set; }

        /// <summary>
        /// 幼崽模板权重表(优先级高于 CubTemplateOverride)。
        /// 键=模板名，值=权重(非百分比，按相对比例计算)。
        /// 例: {"Cow": 1, "Bull": 1} 表示 50% 生 Cow，50% 生 Bull。
        /// 空/null = 回退到 CubTemplateOverride 或沿用母体。
        /// </summary>
        public Dictionary<string, float> CubTemplates { get; set; } = new();

        // ==================== 运行时(不序列化) ====================

        [JsonIgnore]
        public HashSet<Season> ParsedSeasons { get; private set; } = new();

        /// <summary>
        /// 解析后的别名集合(含自身)，用于交配匹配。
        /// 例: Cow 的 MatingSet = {Cow, Bull}；Bull 的 MatingSet = {Bull, Cow}。
        /// 两个个体 MatingSet 有交集即可交配。
        /// </summary>
        [JsonIgnore]
        public HashSet<string> MatingSet { get; private set; } = new();

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

            // 构建交配集合(含自身+别名)
            MatingSet = new HashSet<string>(StringComparer.Ordinal) { /* 自身名由外部 SetSpeciesName 填入 */ };
            Aliases ??= new List<string>();
            foreach (string alias in Aliases)
            {
                if (!string.IsNullOrEmpty(alias))
                {
                    MatingSet.Add(alias);
                }
            }
            CubTemplateOverride = string.IsNullOrEmpty(CubTemplateOverride) ? null : CubTemplateOverride;
            CubTemplates ??= new Dictionary<string, float>();
            // 移除权重<=0 或空模板名的条目
            var keysToRemove = new List<string>();
            foreach (var kv in CubTemplates)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0f) keysToRemove.Add(kv.Key);
            }
            foreach (var k in keysToRemove) CubTemplates.Remove(k);
        }

        /// <summary>由 BreedingConfig.Normalize 阶段调用，把当前物种名加入 MatingSet。</summary>
        internal void SetSpeciesName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                MatingSet.Add(name);
            }
        }
    }
}
