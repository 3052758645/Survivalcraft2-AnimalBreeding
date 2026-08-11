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

---

## 四、添加新物种

只需在 `Species` 下添加对应模板名条目，代码无需改动。例如同时配置灰狼和牛：

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
    "Cow": {
      "BreedingSeasons": [ "Spring", "Summer" ],
      "CubDurationDays": 2,
      "GestationSeconds": 60.0,
      "AdultMaleBoxScale": 1.1,
      "MaleAttackBonus": 1.0,
      "CubAttackFactor": 0.2,
      "AdultAttackFactor": 0.5
    }
  }
}
```

> 模板名必须与 `Database.xml` 中的生物模板名完全一致（如 `Wolf_Gray`、`Cow`、`Hyena` 等）。
> 每个物种可以只写需要修改的参数，未写的会用默认值。

---

## 五、完整配置示例

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

## 六、常见调参场景

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

---

## 七、配置加载机制

- **加载时机**：`OnProjectLoaded` 钩子中调用 `BreedingConfig.Load()`，即世界加载完成时。
- **加载方式**：通过 `ContentManager.Get<string>("BreedingConfig", ".json")` 读取 `MOD/Assets/BreedingConfig.json`。
- **缓存**：加载后存入 `BreedingConfig.Current` 静态属性，运行时直接读取。
- **重载**：修改配置后**退出世界重进**即可重新加载，无需重启游戏。
- **容错**：
  - 配置文件为空 → 繁殖系统禁用（`Enabled=false`）。
  - 配置文件解析失败 → 繁殖系统禁用，日志输出警告。
  - 未知季节字符串 → 忽略并日志警告。
  - `CubDurationDays <= 0` → 自动改为 3 天。
  - `Species` 为 null → 自动初始化为空字典。

---

## 八、参数与代码对应关系

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
