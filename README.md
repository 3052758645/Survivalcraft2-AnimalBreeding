# 动物繁殖系统模组 (Breeding)

为 Survivalcraft 2 (2.4.0.0) 加入完整的动物繁殖系统的独立模组。

## 功能

1. **季节开关** — 每只动物 1~2 个繁殖季节，不在季节内无法触发交配
2. **怀孕成功率三选一失败检测**：
   - 血量 < 40% → 失败
   - 温湿度偏差 > 0.4 → 失败
   - 15×15 范围同类成年 > 8 只 → 失败
   - 否则 70% 成功率
3. **成长阶段** — 幼崽期 3 天 → 成年期；幼崽期每天 30% 概率夭折判定
4. **攻击力动态调整**(直接乘算)：
   - 幼崽 ×0.3 / 成年 ×1.0
   - 发情期 ×0.5 (仇恨范围 ×2 会主动追玩家)
   - 残血(<30%) ×0.5
5. **近亲与重复配对防护** — 只追溯父母双方 ID；记录最近 3 次成功交配对象

## 安装

1. 用 Visual Studio / Rider 打开 `HYKJBreedingMod.csproj`
2. 把游戏目录下的 `Engine.dll` / `EntitySystem.dll` / `Survivalcraft.dll` 复制到 `Quoted/` 目录
3. 编译生成 `HYKJBreedingMod.dll`
4. 把 `MOD/` 目录打包成 `Survivalcraft.HYKJBreeding.pak`
5. 放到游戏的 `Mods/` 目录

## 配置

所有属性配置在 [MOD/Assets/BreedingConfig.json](MOD/Assets/BreedingConfig.json)，退出世界重进即生效，不需要重新编译。

### 全局参数
- `Enabled` — 全局开关
- `PregnancyCooldownDays` — 母体分娩后再次怀孕冷却(游戏天)
- `DefaultPregnancySuccessRate` — 默认怀孕成功率
- `DensityRadius` / `DensityMaxAdults` — 密度检测参数
- `CubAttackFactor` / `AdultAttackFactor` / `EstrusAttackFactor` / `LowHealthAttackFactor` — 攻击力系数
- `InbreedingEnabled` / `RecentMatesLimit` — 近亲与重复配对检测

### 物种配置 (`Species` 字典)
按实体模板名索引(如 `Wolf_Gray`)，每个物种可配置：
- `BreedingSeasons` — 繁殖季节(Summer/Autumn/Winter/Spring)
- `OptimalTemperature` / `OptimalHumidity` — 最适温湿度(0~1)
- `CubDurationDays` — 幼崽期天数
- `CubTemplate` — 幼崽模板名(不存在会降级用父模板+缩小碰撞盒)
- `CubBoxSize` / `AdultBoxSize` — 碰撞盒大小
- `NestBlocks` — 窝/食物源方块名(幼崽存活判定用)

## 已配置的物种

| 模板名 | 中文 | 繁殖季节 |
|---|---|---|
| Wolf_Gray | 灰狼 | 冬季 |
| Wolf_Coyote | 郊狼 | 春、秋 |
| Cow_Brown / Cow_Black | 奶牛 | 春、秋 |
| Bull_Brown / Bull_Black | 公牛 | 春、秋 |
| Horse_Black/Bay/Chestnut/Palomino/White | 马 | 春季 |

## 测试

详见主仓库的测试文档。简要步骤：

1. 创造模式进入世界，确保季节变化开启
2. 用刷怪蛋刷 6~8 只同种动物(如 Cow_Brown)在草地围栏内
3. 等待约 30 秒，查看日志应出现 `[HYKJ.Breeding] 交配成功`
4. 等 1 游戏天后出现 `分娩成功`
5. 等 3 游戏天幼崽进阶成年
