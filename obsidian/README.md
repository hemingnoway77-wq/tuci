# English Night Study

根据用户 2026-10-22 提供的深色参考图配置，用于 Obsidian 的 CSS 片段。无需插件，不修改 Markdown 正文。

- 背景：`#1d2127`，由参考图片中的背景像素取样。
- 主要标题（H1–H4）：蓝色 `#518ff1`。
- 子标题（H5–H6）及加粗文字：粉色 `#e879a6`。
- 正文：浅灰 `#d4d8df`，保留可辨认的弱化文字与列表指引线。
- 字体：Segoe UI，中文回退至本机已安装的 Noto Sans SC（思源黑体），再回退至微软雅黑 UI。
- 当前 Obsidian 正文字号为 16px；片段设定 1.8 倍行高和 760px 正文最大宽度。字号仍由外观设置控制。

已安装到：

```text
C:/Private Files/Obsidian/English/英语单词/.obsidian/snippets/english-night-study.css
```

已通过真实 Obsidian 1.14.4 设置界面将该仓库的基础颜色设为“深色”，启用 `english-night-study`。已查看 introduced 笔记的实时预览和阅读视图，确认深色背景、蓝色标题、粉色强调和中文字体实际生效；不作用于其他仓库。introduced.md SHA-256 仍为 `D4613D8117C8BE9FC34CBD216E190E1763331DE8B7C60231E983A74998C6E7D7`，正文未修改。

如需恢复：在“设置 → 外观 → CSS 样式代码片段”关闭 `english-night-study`；若希望连基础颜色也恢复，改回“跟随系统”。修改前的 appearance.json 备份在项目 `.artifacts/obsidian-style-2026-10-22/appearance.before.json`。

如需调整颜色或行距，可以编辑片段；已启用的 CSS 片段保存后会自动更新，见 [Obsidian 官方说明](https://obsidian.md/help/snippets)。片段优先使用 [官方 CSS 变量](https://docs.obsidian.md/Reference/CSS%20variables/About%20styling)，同时兼容阅读视图和实时预览。
