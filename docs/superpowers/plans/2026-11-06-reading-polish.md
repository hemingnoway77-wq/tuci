# 词库交互与每日纯净阅读

用户已明确功能和截图；保留现有两栏及客户端主题，不再扩为完整工作区。按已有直接实施授权执行。

## 架构和范围

- 词库：点击非交互空白（含导航上方品牌区）移走输入焦点；Markdown 用清晰无衬线、较紧凑标题段距及竖排引用释义；沿用主题语义色；普通悬停内部词链显示简卡，移除原生黑色路径 tooltip。
- 每日阅读：目录一张全宽纸页，顶部刷新按钮，无右侧介绍卡；移除默认虚线focus visual。标题／阅读按钮在本页打开纯净正文，返回恢复目录位置。抓取失败明确报告，绝不把摘要标成全文。
- 纯净正文：真实英文单词按出现位置点击，高亮并弹“翻译／收录”；翻译只发所选单词；收录只预填当前词形和原句到既有捕获页，不自动调用AI或保存。切词／返回／换页取消迟到请求，保持列表和文章状态。
- 参考同仓 `origin/codex/web` 的 `web/reader-words.js`、`word-lookup.js`、`learning.js` 与 `server/LearningWeb.cs`。不合并／改写Web、iOS或server分支，不接第三方文章清洗服务，不使用AI提取正文。

## 接口与文件归属

1. Core `Reading/ArticleContentService.cs` 等（article_reader）：
   - `ArticleContent(ReadingArticle Article,IReadOnlyList<string> Paragraphs,DateTimeOffset FetchedAt,bool IsFromCache=false,bool IsOffline=false,string Message="")`。
   - `IArticleContentService.GetAsync(ReadingArticle article,bool forceRefresh,CancellationToken)`。
   - `ArticleContentException : IOException`；有效缓存可明确离线退回，失败不伪造正文。
   - 自建生产HttpClient（禁自动重定向／cookie），注入构造供隔离测试。固定ScienceDaily／Cloudflare／BBC来源、逐跳验证、8秒deadline、解压后2MiB、12h缓存／80项。AngleSharp解析DOM，不执行脚本或取远程资源；正文段落限额超限明确失败。
2. Core `Reading/WordTranslationService.cs`（learning_core）：
   - `WordTranslation(string Word,string TranslatedText,bool IsFromCache=false)`。
   - `IWordTranslationService.TranslateAsync(string word,CancellationToken)`。
   - `MyMemoryWordTranslationService` 自建生产HttpClient和可注入测试构造；10秒、有限响应、HTTP/JSON/配额检查、成功LRU128、无redirect／cookie／AI Key。只支持规范英文单词，保留词形。
3. Desktop `Reading/ArticleTextReader.cs` 与 ReadingPage（root）：原生可选文本FlowDocument、词按钮弹层、正文与目录切换、取消／返回／纯净正文注入服务。`ReadingWordSelection(string Word,string Context)`；`ReadingPage.WordCaptureRequested` 交MainWindow预填收录。纯净文案、错误、旧列表位置保留，沿用当前全局主题。
4. Preview/NoteReader（note_reader）：字体／段落／引用、普通hover与tooltip，保留选择复制／安全链接／主题动态切换。
5. MainWindow（root）：空白失焦、独立正文和翻译服务生命周期、收录预填；只清空无交互目标焦点，不吞按钮、链接或正文选择事件。

## 验证

- [x] 根因红绿：空白失焦与虚线focus visual；原生路径tooltip；普通悬停、移开、切文、主题与引用分行。
- [x] Core有界请求、来源白名单／重定向、正文广告剔除、超时／取消、缓存、无正文和配额错误。
- [x] WPF目录→纯净正文→点词翻译／预填收录→返回原目录位置；迟到请求不覆盖当前选词／页面，文字可选复制，三主题和最小窗可用。
- [x] 更新旧阅读冒烟和离线mock，真实来源定点检查；完整回归808/808与全部WPF／WebView2检查、截图、README／验收文档、Windows自包含发布及交付校验。

交付分支为 `feature/windows-learning`；Git 同步结果由远端校验和交付回复记录，本文不自证远端状态。
