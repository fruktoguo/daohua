# 交互可视化与 Generative UI 优先准则 (Generative UI First)

本规则规定智能体在向用户呈现复杂方案、数值计算与功能原型时，必须优先以内嵌可交互 HTML 部件的形式直观传达。

---

## 1. 核心准则：拒绝冗长纯文本轰炸
凡是能够通过可视化、动态交互、实时计算或微型应用更好传达意图的场景，**坚决避免输出冗长晦涩的纯 Markdown 文本堆砌**，必须主动编写并就地内联渲染 Generative UI。

---

## 2. 强制触发场景

1. **数值测算与参数调优**：
   - 涉及游戏数值、经济模型、公式曲线、费率折算或概率期望时，必须提供带可拖动 Slider、实时计算输出并带动态进度条的微型计算器。
2. **多套技术方案与架构选型对比**：
   - 存在多种实现方案（方案 A / 方案 B / 方案 C）需用户决策时，提供带选项单选、特征对比表格、优缺点高亮的可视化决策卡片。
3. **流程与状态机流转**：
   - 涉及复杂状态生命周期、审批流、网络信封往返流程时，提供可点击步进（Next/Prev）、高亮当前节点的状态流转部件。
4. **功能原型与操作预览**：
   - 开发前端新交互组件、复杂表单或操作控制台前，先提供一个可供用户就地点击试玩的交互原型卡片。
5. **实时进度与健康看板**：
   - 长效多阶段任务、压测指标监控或环境检查时，提供仪表盘样式的可视化监控卡片。

---

## 3. 编码与集成规范

- **文件存储与元数据**：使用自包含 `.html` 文件写入 Artifacts 目录，并标记 `UserFacing: true`。
- **Tailwind CSS 依赖**：在 `<head>` 中必须引入官方允许的 CDN：
  ```html
  <script src="https://www.gstatic.com/antigravity/web/dev/tailwindcss.min.js"></script>
  ```
- **语义化设计系统（Semantic Design Tokens）**：
  - 严禁硬编码纯黑白；
  - 统一使用宿主语义变量：背景 `bg-[var(--card)]`、前景色 `text-[var(--foreground)]`、浅色文字 `text-[var(--muted-foreground)]`、边框 `border-[var(--border)]`、主色调 `bg-[var(--primary)]`，确保深浅色主题无缝自适应。
- **紧凑视口控制**：
  - 根节点透明：`<body class="bg-transparent text-[var(--foreground)] antialiased p-3">`；
  - 高度预算严格控制在 300px ~ 480px 之间，禁止在顶层使用 `h-screen`、`min-h-screen` 或 `100vh`。
- **就地嵌入**：
  - 在最终回复中，直接使用 `<agent-embed>` 标签就地渲染：
    ```html
    <agent-embed src="file:///<artifact_absolute_path>/widget.html"></agent-embed>
    ```
