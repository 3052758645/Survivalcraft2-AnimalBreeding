# 繁殖系统配置文档 (CONFIG)

本模组所有参数都从 [MOD/Assets/BreedingConfig.json](MOD/Assets/BreedingConfig.json) 读取，**退出世界重进即生效**，无需重新编译。

配置文件使用 JSON 格式。全局只保留 `Enabled` 总开关，**其余所有参数都按物种独立配置**，这样不同生物可以有不同的孕期、体型、攻击力等。

> 以 `_` 开头的字段会被忽略，仅作说明用。所有字段名大小写不敏感。

---

## 一、配置结构

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "BreedingSeasons": [ "Winter" ],
      "CubDurationDays": 3,
      "GestationSeconds": 30.0,
      ...
    }
  }
}
```

- `Enabled`（全局）：总开关，`false` 时繁殖系统完全不生效。
- `Species`（全局）：按实体模板名索引的物种字典，每个物种独立配置所有繁殖参数。

---

## 二、全局参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `true` | 全局总开关。`false` 时繁殖系统完全不生效，所有生物保持原版行为。 |

---

## 三、物种参数（每个物种独立配置）

以下参数都写在 `Species.模板名` 下，例如 `Species.Wolf_Gray`。

### 繁殖季节与成长

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `BreedingSeasons` | string[] | `["Winter"]` | 繁殖季节列表。可选值：`Summer` / `Autumn` / `Winter` / `Spring`。 |
| `CubDurationDays` | float | `3` | 幼崽期持续天数（游戏天）。到期后进阶成年。 |

### 时间参数（单位：现实秒）

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `GestationSeconds` | float | `30.0` | 孕期持续秒数。母体交配成功后此秒数分娩。 |
| `MatingRequiredProximitySeconds` | float | `10.0` | 交配所需相处时间。公母在 `MateRadius` 内持续相处此秒数后触发交配。 |
| `WeaknessSeconds` | float | `60.0` | 虚弱期持续秒数。交配后仅公体虚弱，分娩后母体虚弱。虚弱期间不发情。 |
| `RivalChaseTime` | float | `30.0` | 公体竞争时追击竞争对手的时长。多公追同一母狼时互相攻击的持续时间。 |

### 距离参数（单位：方块）

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `MateRadius` | float | `2.0` | 交配判定半径。公母在此距离内持续相处才算交配。 |
| `SeekRadius` | float | `20.0` | 公体寻找母体的搜索半径。公体发情时在此范围内寻找母体并走过去。 |
| `BirthSpawnOffset` | float | `1.5` | 分娩时幼崽在母体附近的随机偏移范围。 |

### 攻击力参数

攻击力公式：`最终攻击力 = 基础攻击力 × 阶段系数 × 性别系数`

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `CubAttackFactor` | float | `0.3` | 幼崽攻击力系数。 |
| `AdultAttackFactor` | float | `1.0` | 成年攻击力系数（基准）。 |
| `MaleAttackBonus` | float | `1.3` | 公体攻击力额外倍率（母体为 1.0）。 |

**示例**：
- 成年公狼：基础 × 1.0 × 1.3 = **1.3×**
- 成年母狼：基础 × 1.0 × 1.0 = **1.0×**
- 幼狼（公）：基础 × 0.3 × 1.3 = **0.39×**

### 体型参数

体型公式：`scale = CubBoxScale + (成年scale - CubBoxScale) × 成长进度`

同时作用于碰撞盒（BoxSize）和视觉模型（ModelScale）。

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `CubBoxScale` | float | `0.5` | 幼崽出生时体型缩放（相对原版）。 |
| `AdultMaleBoxScale` | float | `1.3` | 成年公体体型缩放（相对原版）。 |
| `AdultFemaleBoxScale` | float | `1.0` | 成年母体体型缩放（相对原版）。 |

### 仇恨与性别参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `EstrusChaseRangeMultiplier` | float | `2.0` | 发情期仇恨范围倍率（乘到 ChaseRange 上）。 |
| `CubMaleProbability` | float | `0.5` | 幼崽/自然生成个体的公体概率（0~1）。`0`=全母，`1`=全公，`0.5`=各半。 |

> 幼崽和怀孕母狼的仇恨范围固定为 0（不产生仇恨），不受此参数影响。

### 交互拦截参数（可骑乘/可上鞍物种专用）

控制繁殖期间和幼崽期间是否禁止玩家对生物交互（上鞍 + 骑乘）。仅对可上鞍/可骑乘物种有意义（Horse/Donkey/Camel/Reindeer/Ostrich），对其他物种配置无害但无实际效果。

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `BlockInteractDuringBreeding` | bool | `true` | 繁殖期间（发情/怀孕/虚弱）是否禁止交互（上鞍+骑乘）。 |
| `BlockInteractDuringCub` | bool | `true` | 幼崽期是否禁止交互（上鞍+骑乘）。 |
| `ConsumeSaddleOnBlocked` | bool | `false` | 上鞍被拦截时是否仍消耗玩家手中的鞍。详见下方说明。 |

> **`ConsumeSaddleOnBlocked` 详细说明**：
> - `false`（默认）：鞍退回玩家。**但当前 mod API 无 `OnUse` hook，原版 `SubsystemSaddleBlockBehavior.OnUse` 在调用我们 hook 前已经扣鞍，因此实际行为是"鞍已扣但上鞍被撤销"。** 真正退鞍需要等官方加 `OnUse` hook 或改用 Harmony patch。
> - `true`：鞍被扣掉但马没上鞍（作为惩罚，玩家会看到"该生物无法上鞍"日志）。
> - **骑乘拦截无此问题**：`ScoreMount` hook 是干净的，被拦截时玩家根本无法骑上，无任何副作用。

### 物种别名与幼崽模板参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Aliases` | string[] | `[]` | 物种别名列表。当前模板可与此列表中的模板**互相交配**。需双向配置。 |
| `CubTemplateOverride` | string | `null` | 幼崽生成时使用的模板名。空或 null = 沿用母体模板（默认）。 |

> **`Aliases` 用法**：让两个不同模板互相交配。例如 `Cow` 配 `Aliases=["Bull"]`、`Bull` 配 `Aliases=["Cow"]`，则母牛可和公牛交配。**必须双向配置**，否则只有一方识别。
>
> **`CubTemplateOverride` 用法**：控制幼崽用什么模板。默认沿用母体（母牛生小母牛，母公牛生小公牛）。若想让母牛只生 `Cow` 模板幼崽，配 `CubTemplateOverride="Cow"`。

---

## 四、已支持的物种

模组开箱即用支持以下物种（模板名必须与 `Database.xml` 完全一致）：

| 模板名 | 中文 | 繁殖季节 | 幼崽期 | 孕期 | 体型(公/母) | 攻击力倍率(公/母) | 可骑/可鞍 | 备注 |
|--------|------|---------|--------|------|------------|------------------|----------|------|
| `Wolf_Gray` | 灰狼 | 冬季 | 3 天 | 30 秒 | 1.3× / 1.0× | 1.3× / 1.0× | ❌ | 发情期仇恨 ×2，攻击性强 |
| `Horse_Black` | 黑马 | 春季 | 5 天 | 60 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 温顺草食，公马会争母马 |
| `Horse_Bay` | 栗色马 | 春季 | 5 天 | 60 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 同上 |
| `Horse_Chestnut` | 红栗色马 | 春季 | 5 天 | 60 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 同上 |
| `Horse_Palomino` | 金色马(热带) | 春季 | 5 天 | 60 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 同上 |
| `Horse_White` | 白马(寒带) | 春季 | 5 天 | 60 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 同上 |
| `Cow` | 母牛 | 春/夏 | 4 天 | 90 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ❌ | 与 Bull 互通交配，只生小母牛 |
| `Bull` | 公牛 | 春/夏 | 4 天 | 90 秒 | 1.2× / 1.0× | 0.78× / 0.6× | ❌ | 与 Cow 互通交配，只生小公牛 |
| `Donkey` | 驴 | 春季 | 4 天 | 70 秒 | 1.05× / 1.0× | 0.55× / 0.5× | ✅ | 繁殖期/幼崽期禁止上鞍骑乘 |
| `Camel` | 骆驼 | 夏季 | 5 天 | 100 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 同上，孕期最长 |
| `Reindeer` | 驯鹿 | 冬季 | 4 天 | 80 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 寒带生物，冬季发情 |
| `Ostrich` | 鸵鸟 | 春季 | 3 天 | 60 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ✅ | 热带生物，繁殖快 |
| `Alpaca` | 羊驼 | 春/秋 | 3 天 | 70 秒 | 1.05× / 1.0× | 0.33× / 0.3× | ❌ | 温顺，可剪毛 |
| `Gnu` | 角马 | 秋季 | 3 天 | 70 秒 | 1.1× / 1.0× | 0.6× / 0.5× | ❌ | 草食群居 |
| `Bison` | 野牛 | 秋季 | 4 天 | 90 秒 | 1.15× / 1.0× | 0.91× / 0.7× | ❌ | 攻击性中等，体型大 |

> **马变种说明**：游戏会自然生成 5 个马变种（`Horse_Black`/`Horse_Bay`/`Horse_Chestnut`/`Horse_Palomino`/`Horse_White`），带鞍的 `*_Saddled` 是玩家驯服后产生的，不参与自然繁殖。**不同变种之间不能交配**（白马只和白马、黑马只和黑马），幼崽沿用母体变种，不会混血。
>
> **Cow/Bull 互通说明**：`Cow`(母牛) 和 `Bull`(公牛) 是两个独立模板，通过 `Aliases` 配置互通交配。母牛只生 `Cow` 模板幼崽（`CubTemplateOverride="Cow"`），公牛只生 `Bull` 模板幼崽。因此牛场需同时养母牛群和公牛群，母牛会生小母牛。
>
> **可骑乘物种交互拦截**：`Horse`/`Donkey`/`Camel`/`Reindeer`/`Ostrich` 在繁殖期（发情/怀孕/虚弱）和幼崽期禁止上鞍+骑乘。详见上文"交互拦截参数"。

---

## 五、添加新物种

只需在 `Species` 下添加对应模板名条目，代码无需改动。例如同时配置灰狼和马：

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "BreedingSeasons": [ "Winter" ],
      "CubDurationDays": 3,
      "GestationSeconds": 30.0,
      "AdultMaleBoxScale": 1.3,
      "MaleAttackBonus": 1.3
    },
    "Horse_White": {
      "BreedingSeasons": [ "Spring" ],
      "CubDurationDays": 5,
      "GestationSeconds": 60.0,
      "AdultMaleBoxScale": 1.1,
      "MaleAttackBonus": 1.2,
      "CubAttackFactor": 0.2,
      "AdultAttackFactor": 0.5
    }
  }
}
```

> 模板名必须与 `Database.xml` 中的生物模板名完全一致（如 `Wolf_Gray`、`Horse_White`、`Hyena` 等）。
> 每个物种可以只写需要修改的参数，未写的会用默认值。

---

## 六、完整配置示例

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "BreedingSeasons": [ "Winter" ],
      "CubDurationDays": 3,
      "GestationSeconds": 30.0,
      "MatingRequiredProximitySeconds": 10.0,
      "WeaknessSeconds": 60.0,
      "RivalChaseTime": 30.0,
      "MateRadius": 2.0,
      "SeekRadius": 20.0,
      "BirthSpawnOffset": 1.5,
      "CubAttackFactor": 0.3,
      "AdultAttackFactor": 1.0,
      "MaleAttackBonus": 1.3,
      "CubBoxScale": 0.5,
      "AdultMaleBoxScale": 1.3,
      "AdultFemaleBoxScale": 1.0,
      "EstrusChaseRangeMultiplier": 2.0,
      "CubMaleProbability": 0.5
    }
  }
}
```

---

## 七、常见调参场景

### 1. 加快灰狼测试速度

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "BreedingSeasons": [ "Winter", "Spring", "Summer", "Autumn" ],
      "CubDurationDays": 0.5,
      "GestationSeconds": 10.0,
      "MatingRequiredProximitySeconds": 3.0,
      "WeaknessSeconds": 15.0
    }
  }
}
```

### 2. 让公狼更强势

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "MaleAttackBonus": 1.8,
      "AdultMaleBoxScale": 1.5,
      "RivalChaseTime": 60.0
    }
  }
}
```

### 3. 让狼群更温顺

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "EstrusChaseRangeMultiplier": 1.0,
      "CubAttackFactor": 0.1,
      "AdultAttackFactor": 0.7
    }
  }
}
```

### 4. 加快马的测试速度

让所有马变种全年发情、快速繁殖，方便观察马驹成长：

```json
{
  "Enabled": true,
  "Species": {
    "Horse_Black":   { "BreedingSeasons": [ "Spring", "Summer", "Autumn", "Winter" ], "CubDurationDays": 0.5, "GestationSeconds": 15.0, "MatingRequiredProximitySeconds": 3.0, "WeaknessSeconds": 15.0 },
    "Horse_Bay":     { "BreedingSeasons": [ "Spring", "Summer", "Autumn", "Winter" ], "CubDurationDays": 0.5, "GestationSeconds": 15.0, "MatingRequiredProximitySeconds": 3.0, "WeaknessSeconds": 15.0 },
    "Horse_Chestnut":{ "BreedingSeasons": [ "Spring", "Summer", "Autumn", "Winter" ], "CubDurationDays": 0.5, "GestationSeconds": 15.0, "MatingRequiredProximitySeconds": 3.0, "WeaknessSeconds": 15.0 },
    "Horse_Palomino":{ "BreedingSeasons": [ "Spring", "Summer", "Autumn", "Winter" ], "CubDurationDays": 0.5, "GestationSeconds": 15.0, "MatingRequiredProximitySeconds": 3.0, "WeaknessSeconds": 15.0 },
    "Horse_White":   { "BreedingSeasons": [ "Spring", "Summer", "Autumn", "Winter" ], "CubDurationDays": 0.5, "GestationSeconds": 15.0, "MatingRequiredProximitySeconds": 3.0, "WeaknessSeconds": 15.0 }
  }
}
```

---

## 八、配置加载机制

- **加载时机**：`OnProjectLoaded` 钩子中调用 `BreedingConfig.Load()`，即世界加载完成时。
- **加载方式（多源合并）**：
  1. 先通过 `ContentManager.Get<string>("BreedingConfig", ".json")` 读取**主配置** `MOD/Assets/BreedingConfig.json`，确定 `Enabled` 总开关。
  2. 再遍历 `ContentManager.List()`，找出所有**扩展配置** `BreedingConfig.{ModId}.json`，按文件名排序后逐个合并 `Species`。
- **缓存**：合并后存入 `BreedingConfig.Current` 静态属性，运行时直接读取。
- **重载**：修改任意配置后**退出世界重进**即可重新加载，无需重启游戏。
- **容错**：
  - 主配置为空 → 繁殖系统禁用（`Enabled=false`）。
  - 主配置解析失败 → 繁殖系统禁用，日志输出警告。
  - 扩展配置为空/无 Species/解析失败 → 跳过该扩展，不影响其他配置。
  - 未知季节字符串 → 忽略并日志警告。
  - `CubDurationDays <= 0` → 自动改为 3 天。
  - `Species` 为 null → 自动初始化为空字典。

---

## 九、第三方模组接入（多源配置）

本模组支持**多源配置合并**：其他模组可以自带一份繁殖配置文件，无需修改本模组代码、无需手动合并配置。

### 文件命名规则

| 类型 | 文件名 | 作用 |
|------|--------|------|
| 主配置 | `BreedingConfig.json` | 决定 `Enabled` 总开关 + 自带物种。仅本模组提供。 |
| 扩展配置 | `BreedingConfig.{ModId}.json` | 第三方模组自带，**仅追加 `Species`**。`{ModId}` 建议用模组唯一标识，避免重名。 |

> 例：`BreedingConfig.CowMod.json`、`BreedingConfig.HyenaPack.json`

### 合并规则

1. **先加载主配置** `BreedingConfig.json`，确定 `Enabled` 和主物种。
2. **再按文件名排序加载扩展配置**（顺序稳定，便于排查冲突）。
3. **同名模板冲突**：主配置永远优先；扩展之间先到先得，后者打 `Warning` 日志并跳过。
4. **扩展配置中的 `Enabled` 字段被忽略**（防止第三方模组意外关闭整个繁殖系统）。
5. **扩展配置可省略所有非 Species 字段**，只写 `Species` 即可。

### 第三方模组接入步骤

1. **确认生物满足前提**：
   - 模板已注册到 `DatabaseManager`（即 `entity.ValuesDictionary.DatabaseObject?.Name` 能拿到模板名）。
   - 生物有 `ComponentCreature` / `ComponentBody` / `ComponentSpawn` / `ComponentModel` / `ComponentFactors` 组件。
2. **在第三方模组的 `MOD/Assets/` 下**放一份 `BreedingConfig.{你的模组Id}.json`：
   ```json
   {
     "Species": {
       "Cow": {
         "BreedingSeasons": [ "Spring", "Summer" ],
         "CubDurationDays": 2,
         "GestationSeconds": 60.0,
         "AdultMaleBoxScale": 1.1,
         "MaleAttackBonus": 1.0,
         "CubAttackFactor": 0.2,
         "AdultAttackFactor": 0.5
       },
       "Bull": {
         "BreedingSeasons": [ "Autumn" ],
         "CubDurationDays": 4
       }
     }
   }
   ```
3. **打包发布**：用户同时安装本繁殖模组和你的模组即可，配置会自动合并。
4. **冲突排查**：游戏日志会输出每个扩展配置的合并结果，例如：
   ```
   [Breeding] 主配置加载完成，物种数=6，Enabled=True
   [Breeding] 发现 1 个扩展配置文件
   [Breeding] 扩展配置 BreedingConfig.CowMod.json 合并完成：新增 2 个物种，跳过 0 个冲突
   [Breeding] 全部配置合并完成，总物种数=8，Enabled=True
   ```
   若有冲突，会看到：
   ```
   [Breeding] 扩展配置 BreedingConfig.CowMod.json 的物种 'Wolf_Gray' 与主配置/先加载的扩展冲突，跳过
   ```

### 注意事项

- **扩展配置只能追加新物种，不能修改主配置已有物种的参数**。如需覆盖，请直接编辑主配置 `BreedingConfig.json`。
- **`{ModId}` 不要用 `Wolf_Gray` 这种模板名**，建议用模组包名/作者名，避免和别人的扩展重名导致排序混乱。
- **不写 `Enabled` 字段**：扩展配置写了也会被忽略，总开关只认主配置。
- **可向后兼容**：旧版本只读 `BreedingConfig.json`，扩展文件会被忽略，不会报错。

---

## 十、参数与代码对应关系

| 配置参数 | 代码位置 | 用途 |
|---------|---------|------|
| `Enabled` | `OnFactorsUpdate` / `OnEntityAdd` 等 | 全局开关 |
| `BreedingSeasons` | `OnFactorsUpdate` 发情判定 | 繁殖季节 |
| `CubDurationDays` | `UpdateGrowth` / `GetGrowthProgress` | 幼崽期天数 |
| `GestationSeconds` | `UpdateFemale` 交配成功时 | 设置孕期倒计时 |
| `MatingRequiredProximitySeconds` | `UpdateFemale` | 交配所需相处时间 |
| `WeaknessSeconds` | `UpdateFemale` 交配/分娩时 | 虚弱期时长 |
| `RivalChaseTime` | `UpdateMale` 竞争时 | 公体竞争追击时长 |
| `MateRadius` | `FindNearbyEstrusMale` | 交配判定半径 |
| `SeekRadius` | `UpdateMale` / `FindRival` | 公体寻路搜索半径 |
| `BirthSpawnOffset` | `GiveBirth` | 分娩幼崽偏移范围 |
| `CubAttackFactor` | `OnMinerHit` | 幼崽攻击力系数 |
| `AdultAttackFactor` | `OnMinerHit` | 成年攻击力系数 |
| `MaleAttackBonus` | `OnMinerHit` | 公体攻击力额外倍率 |
| `CubBoxScale` | `ApplyBoxSizeByGrowth` | 幼崽出生体型缩放 |
| `AdultMaleBoxScale` | `ApplyBoxSizeByGrowth` | 成年公体体型缩放 |
| `AdultFemaleBoxScale` | `ApplyBoxSizeByGrowth` | 成年母体体型缩放 |
| `EstrusChaseRangeMultiplier` | `ApplyChaseRangeFactor` | 发情期仇恨范围倍率 |
| `CubMaleProbability` | `OnEntityAdd` / `GiveBirth` | 公体生成概率 |
