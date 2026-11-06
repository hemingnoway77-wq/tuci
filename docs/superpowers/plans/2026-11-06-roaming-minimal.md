# 漫游精简与弹性布局

按本轮标号截图直接实施，沿用已有 worktree、主题、统计口径和用户直接修改授权。

- [x] HoldToReveal：仅正文真实模糊，无边框、遮层、提示或焦点矩形；保留按住／释放／离开／空格等交互与可访问性语义，显示前后布局不跳动。
- [x] 状态栏：移除滚动容器，标题／权重常驻。按内容真实测量，变矮先整体隐藏每日活动，再隐藏复习覆盖，拉高按相反顺序恢复，不修改统计／周期选择。清除装饰口号和功能说明，只保留标题、数据、按钮及当天已记住状态。
- [x] PeriodSelector：原生周月选择，单根短细淡线，线距文字更远。方向性伸展到目标后收拢，快速反切承接当前位置，系统禁用动画时直接到终点，键盘与辅助功能可选。
- [x] 回归与真实 WPF：三种高度的隐藏顺序／恢复、无遮罩边框／解释、动画中间态／快速切换／停止动画、三主题与三栏最小窗；完整发布检查849/849及 EXE／ZIP 哈希交付。

接口：`PeriodSelector.IsMonthly` bool DP、`AnimateTransitions` bool DP、`SelectionChanged`、`WeekButton`／`MonthButton`、`Dispose`；共享线名 `PeriodUnderline`。LearningPage 保留原按钮名称别名与统计接口，Core 计数逻辑不改。
