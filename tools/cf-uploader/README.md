# 道化太初 · 专属表情与图片图床 (Cloudflare R2 + Worker)

本目录为聊天表情包与外链图片专属图床服务，基于 Cloudflare R2（0 流量费永久存储）与 Cloudflare Worker（全自包含分发与边缘缓存）。

## 线上服务信息

- **线上服务地址**：`https://daohua-emotes.fruktoyao.workers.dev`
- **绑定的 R2 存储桶**：`chat-emotes`
- **Web 上传面板**：浏览器直接打开 `https://daohua-emotes.fruktoyao.workers.dev`，支持文件选择、拖拽及 `Ctrl + V` 剪贴板粘贴。
- **上传接口**：`POST https://daohua-emotes.fruktoyao.workers.dev/upload`（支持 `multipart/form-data` 或二进制直接 POST）
- **直链分发**：`GET https://daohua-emotes.fruktoyao.workers.dev/:filename`（自动附带 `Cache-Control: public, max-age=31536000, immutable` 与 `Access-Control-Allow-Origin: *`）

## 运维与二次部署命令

本地已完成 `npx wrangler login` 授权。若后续修改了 `src/index.js`，在当前目录执行以下命令即可快速热更新：

```bash
cd tools/cf-uploader
npx wrangler deploy
```

## 自定义域名绑定（可选）

如需绑定个人域名（如 `img.yuohira.com`）：
1. 登录 Cloudflare 控制台 -> **Workers 和 Pages** -> 找到 `daohua-emotes`。
2. 进入 **设置 (Settings)** -> **触发器 (Triggers)** -> **自定义域 (Custom Domains)**。
3. 添加你的子域名（如 `img.yuohira.com`），Cloudflare 会自动完成 DNS 解析与 HTTPS 证书签发。
