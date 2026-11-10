const DEFAULT_SECRET = "daohua-chat-image-secret-2026";

const HTML_PAGE = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>道化太初 · 传音表情图床</title>
  <style>
    :root {
      --bg: #0f141c;
      --card: #18202c;
      --border: #283548;
      --accent: #58a6ff;
      --accent-hover: #79b8ff;
      --text: #e6edf3;
      --muted: #8b949e;
      --success: #3fb950;
      --danger: #f85149;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      background: var(--bg);
      color: var(--text);
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      padding: 20px;
    }
    .container {
      width: 100%;
      max-width: 520px;
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 12px;
      padding: 32px 24px;
      box-shadow: 0 16px 36px rgba(0,0,0,0.4);
      text-align: center;
    }
    h1 { font-size: 20px; margin-bottom: 8px; font-weight: 600; color: #fff; }
    p.desc { font-size: 13px; color: var(--muted); margin-bottom: 20px; }
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      background: rgba(88, 166, 255, 0.1);
      border: 1px solid rgba(88, 166, 255, 0.3);
      color: var(--accent);
      padding: 4px 10px;
      border-radius: 20px;
      font-size: 12px;
      margin-bottom: 20px;
    }
    .ticket-input-box {
      margin-bottom: 16px;
      text-align: left;
    }
    .ticket-input-box label {
      display: block;
      font-size: 12px;
      color: var(--muted);
      margin-bottom: 6px;
    }
    .ticket-input {
      width: 100%;
      background: #0d1117;
      border: 1px solid var(--border);
      border-radius: 6px;
      color: #c9d1d9;
      font-family: monospace;
      font-size: 12px;
      padding: 8px 10px;
      outline: none;
    }
    .ticket-input:focus { border-color: var(--accent); }
    .dropzone {
      border: 2px dashed var(--border);
      border-radius: 8px;
      padding: 36px 16px;
      cursor: pointer;
      transition: all 0.2s ease;
      background: rgba(255, 255, 255, 0.02);
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 10px;
    }
    .dropzone:hover, .dropzone.dragover {
      border-color: var(--accent);
      background: rgba(88, 166, 255, 0.06);
    }
    .icon { font-size: 32px; }
    .hint { font-size: 14px; color: var(--text); }
    .subhint { font-size: 12px; color: var(--muted); }
    input[type="file"] { display: none; }
    .result {
      margin-top: 24px;
      display: none;
      flex-direction: column;
      gap: 12px;
      text-align: left;
    }
    .preview-box {
      display: flex;
      align-items: center;
      justify-content: center;
      background: rgba(0,0,0,0.3);
      border-radius: 8px;
      padding: 12px;
      max-height: 200px;
      overflow: hidden;
    }
    .preview-box img {
      max-width: 100%;
      max-height: 180px;
      object-fit: contain;
      border-radius: 4px;
    }
    .url-box {
      display: flex;
      gap: 8px;
    }
    .url-input {
      flex: 1;
      background: #0d1117;
      border: 1px solid var(--border);
      border-radius: 6px;
      color: #7ee787;
      font-family: monospace;
      font-size: 13px;
      padding: 8px 12px;
      outline: none;
    }
    .btn {
      background: var(--accent);
      color: #0d1117;
      border: none;
      border-radius: 6px;
      font-weight: 600;
      padding: 8px 16px;
      font-size: 13px;
      cursor: pointer;
      transition: background 0.15s ease;
      white-space: nowrap;
    }
    .btn:hover { background: var(--accent-hover); }
    .status-text { font-size: 13px; color: var(--muted); margin-top: 12px; }
    .status-text.error { color: var(--danger); }
  </style>
</head>
<body>
  <div class="container">
    <h1>道化太初 · 传音表情图床</h1>
    <p class="desc">SHA-256 哈希去重复用 · 账号配额保护 · 毫秒直链</p>
    
    <div class="badge">🛡️ 已启用道化主服 HMAC 鉴权防盗刷保护</div>

    <div class="ticket-input-box">
      <label for="ticketInput">上传凭据 (Upload Ticket，游戏内粘贴会自动申请)：</label>
      <input type="text" class="ticket-input" id="ticketInput" placeholder="直接网页测试请填入由主服签发的 Ticket...">
    </div>

    <div class="dropzone" id="dropzone">
      <div class="icon">🖼️</div>
      <div class="hint">点击选择图片 / 拖拽图片至此</div>
      <div class="subhint">或直接在页面中按下 <b>Ctrl + V</b> 粘贴截图/动图</div>
      <input type="file" id="fileInput" accept="image/*">
    </div>

    <div class="status-text" id="statusText"></div>

    <div class="result" id="result">
      <div class="preview-box">
        <img id="previewImg" src="" alt="预览">
      </div>
      <div class="url-box">
        <input type="text" class="url-input" id="urlInput" readonly>
        <button class="btn" id="copyBtn">复制直链</button>
      </div>
    </div>
  </div>

  <script>
    const dropzone = document.getElementById('dropzone');
    const fileInput = document.getElementById('fileInput');
    const ticketInput = document.getElementById('ticketInput');
    const statusText = document.getElementById('statusText');
    const result = document.getElementById('result');
    const previewImg = document.getElementById('previewImg');
    const urlInput = document.getElementById('urlInput');
    const copyBtn = document.getElementById('copyBtn');

    dropzone.addEventListener('click', () => fileInput.click());
    fileInput.addEventListener('change', (e) => {
      if (e.target.files?.[0]) upload(e.target.files[0]);
    });

    ['dragenter', 'dragover'].forEach(name => {
      dropzone.addEventListener(name, (e) => { e.preventDefault(); dropzone.classList.add('dragover'); });
    });
    ['dragleave', 'drop'].forEach(name => {
      dropzone.addEventListener(name, (e) => { e.preventDefault(); dropzone.classList.remove('dragover'); });
    });
    dropzone.addEventListener('drop', (e) => {
      const file = e.dataTransfer.files?.[0];
      if (file && file.type.startsWith('image/')) upload(file);
    });

    window.addEventListener('paste', (e) => {
      const items = e.clipboardData?.items;
      if (!items) return;
      for (const item of items) {
        if (item.type.startsWith('image/')) {
          upload(item.getAsFile());
          break;
        }
      }
    });

    async function upload(file) {
      statusText.className = 'status-text';
      statusText.innerText = '正在上传...';
      result.style.display = 'none';

      const formData = new FormData();
      formData.append('file', file);

      const headers = {};
      const ticket = ticketInput.value.trim();
      if (ticket) headers['X-Upload-Ticket'] = ticket;

      try {
        const res = await fetch('/upload', {
          method: 'POST',
          headers,
          body: formData
        });
        if (!res.ok) {
          const errText = await res.text();
          throw new Error(\`[HTTP \${res.status}] \${errText}\`);
        }
        const url = (await res.text()).trim();

        previewImg.src = url;
        urlInput.value = url;
        result.style.display = 'flex';
        statusText.innerText = '上传成功！';

        navigator.clipboard.writeText(url).then(() => {
          copyBtn.innerText = '已复制！';
          setTimeout(() => copyBtn.innerText = '复制直链', 2000);
        });
      } catch (err) {
        statusText.className = 'status-text error';
        statusText.innerText = '上传失败: ' + err.message;
      }
    }

    copyBtn.addEventListener('click', () => {
      urlInput.select();
      navigator.clipboard.writeText(urlInput.value).then(() => {
        copyBtn.innerText = '已复制！';
        setTimeout(() => copyBtn.innerText = '复制直链', 2000);
      });
    });
  </script>
</body>
</html>`;

/**
 * 将 Base64Url 字符串解码为 Uint8Array
 */
function base64UrlToBytes(base64Url) {
  let base64 = base64Url.replace(/-/g, "+").replace(/_/g, "/");
  while (base64.length % 4) base64 += "=";
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

/**
 * 校验主服签发的 HMAC-SHA256 Ticket
 */
async function verifyTicket(ticketStr, secret) {
  if (!ticketStr || !ticketStr.includes(".")) return { valid: false, reason: "Ticket 格式无效" };
  const [payloadB64, sigB64] = ticketStr.split(".");
  if (!payloadB64 || !sigB64) return { valid: false, reason: "Ticket 字段不完整" };

  try {
    const rawKey = new TextEncoder().encode(secret);
    const key = await crypto.subtle.importKey(
      "raw",
      rawKey,
      { name: "HMAC", hash: "SHA-256" },
      false,
      ["verify"]
    );

    const sigBytes = base64UrlToBytes(sigB64);
    const dataBytes = new TextEncoder().encode(payloadB64);
    const isValidSig = await crypto.subtle.verify("HMAC", key, sigBytes, dataBytes);
    if (!isValidSig) return { valid: false, reason: "Ticket 签名不匹配，疑似篡改" };

    const payloadJson = new TextDecoder().decode(base64UrlToBytes(payloadB64));
    const payload = JSON.parse(payloadJson);

    // 检查时效（5分钟）
    if (!payload.exp || Date.now() > payload.exp) {
      return { valid: false, reason: "Ticket 已过期，请重试" };
    }

    if (!payload.hash || typeof payload.hash !== "string") {
      return { valid: false, reason: "Ticket 缺少图片哈希" };
    }

    return { valid: true, payload };
  } catch (err) {
    return { valid: false, reason: `Ticket 校验异常: ${err.message}` };
  }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const corsHeaders = {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type, X-Upload-Ticket, Authorization",
    };

    // 1. CORS 跨域预检
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }

    // 2. 首页渲染说明与受控上传面板
    if ((request.method === "GET" || request.method === "HEAD") && (url.pathname === "/" || url.pathname === "/index.html")) {
      return new Response(request.method === "HEAD" ? null : HTML_PAGE, {
        headers: { "Content-Type": "text/html; charset=utf-8" },
      });
    }

    // 3. 上传接口 (POST /upload 或 POST /)
    if (request.method === "POST" && (url.pathname === "/" || url.pathname === "/upload")) {
      const secret = env.IMAGE_SECRET || DEFAULT_SECRET;

      // 提取 Ticket：支持 X-Upload-Ticket 请求头或 Authorization: Bearer
      let ticketStr = request.headers.get("X-Upload-Ticket");
      if (!ticketStr) {
        const auth = request.headers.get("Authorization") || "";
        if (auth.startsWith("Bearer ")) ticketStr = auth.slice(7).trim();
      }

      if (!ticketStr) {
        return new Response("未授权：上传必须携带道化主服签发的 X-Upload-Ticket 凭证", {
          status: 401,
          headers: corsHeaders,
        });
      }

      // 校验 Ticket 签名与时效
      const authResult = await verifyTicket(ticketStr, secret);
      if (!authResult.valid) {
        return new Response(`鉴权拒绝: ${authResult.reason}`, {
          status: 403,
          headers: corsHeaders,
        });
      }

      const { payload } = authResult;

      try {
        const contentType = request.headers.get("Content-Type") || "";
        let fileData;
        let mimeType = "image/png";

        if (contentType.includes("multipart/form-data")) {
          const formData = await request.formData();
          const file = formData.get("file") || formData.get("fileToUpload") || formData.get("image");
          if (!file) {
            return new Response("缺少文件 payload", { status: 400, headers: corsHeaders });
          }
          fileData = await file.arrayBuffer();
          mimeType = file.type || "image/png";
        } else {
          fileData = await request.arrayBuffer();
          mimeType = contentType || "image/png";
        }

        if (!fileData || fileData.byteLength === 0) {
          return new Response("文件内容为空", { status: 400, headers: corsHeaders });
        }

        // 计算上传二进制文件的实际 SHA-256 哈希
        const digest = await crypto.subtle.digest("SHA-256", fileData);
        const actualHash = Array.from(new Uint8Array(digest))
          .map((b) => b.toString(16).padStart(2, "0"))
          .join("");

        // 校验文件哈希是否与 Ticket 承诺的哈希完全一致（杜绝拿着 A 图的 Ticket 传 B 图）
        if (actualHash.toLowerCase() !== payload.hash.toLowerCase()) {
          return new Response("文件哈希与 Ticket 签名不符，拒绝落盘", {
            status: 403,
            headers: corsHeaders,
          });
        }

        const ext = payload.ext || "png";
        // 基于哈希前 16 位确定文件名，同图绝对同名，全服自动物理去重
        const filename = `${actualHash.slice(0, 16)}.${ext}`;

        // 检查 R2 是否已有该文件
        await env.MY_BUCKET.put(filename, fileData, {
          httpMetadata: {
            contentType: mimeType,
            cacheControl: "public, max-age=31536000, immutable",
          },
        });

        let fastUrl = `${url.origin}/${filename}`;
        try {
          // 尝试同步上传至国内高速图床（路过图床 ax1x.com CDN，国内秒开，14天免登录外链）
          const pageRes = await fetch("https://imgchr.com/", {
            headers: {
              "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
            },
          });
          if (pageRes.ok) {
            const pageHtml = await pageRes.text();
            const cookies = [];
            pageRes.headers.forEach((value, key) => {
              if (key.toLowerCase() === "set-cookie") cookies.push(value);
            });
            const cookieStr = cookies.length ? cookies.map(c => c.split(";")[0]).join("; ") : "";
            const tokenMatch = pageHtml.match(/PF\.obj\.config\.auth_token\s*=\s*"([^"]+)"/);
            const authToken = tokenMatch ? tokenMatch[1] : null;

            const uploadForm = new FormData();
            uploadForm.append("source", new Blob([fileData], { type: mimeType }), filename);
            uploadForm.append("type", "file");
            uploadForm.append("action", "upload");
            if (authToken) uploadForm.append("auth_token", authToken);

            const uploadRes = await fetch("https://imgchr.com/json", {
              method: "POST",
              headers: {
                "Cookie": cookieStr,
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
              },
              body: uploadForm,
            });
            if (uploadRes.ok) {
              const uploadJson = await uploadRes.json();
              if (uploadJson.status_code === 200 && uploadJson.image?.url) {
                fastUrl = uploadJson.image.url;
              }
            }
          }
        } catch (e) {
          // 降级使用 R2 直链
        }

        return new Response(fastUrl, {
          status: 200,
          headers: {
            ...corsHeaders,
            "Content-Type": "text/plain; charset=utf-8",
          },
        });
      } catch (err) {
        return new Response(`上传处理异常: ${err.message}`, {
          status: 500,
          headers: corsHeaders,
        });
      }
    }

    // 4. 图片直链读取 (GET /:filename 或 HEAD /:filename)
    if (request.method === "GET" || request.method === "HEAD") {
      const key = decodeURIComponent(url.pathname.slice(1));
      if (!key) {
        return new Response("Not Found", { status: 404, headers: corsHeaders });
      }

      let object = request.method === "HEAD"
        ? await env.MY_BUCKET.head(key)
        : await env.MY_BUCKET.get(key);

      // 表情自愈缓存：若 R2 暂未缓存 emotes/*，自动从源站拉取并写入 R2 永久持久化
      if (!object && key.startsWith("emotes/")) {
        const filename = key.replace(/^emotes\//, "");
        const upstreamUrl = `https://koishi.js.org/QFace/gif/${filename}`;
        try {
          const upstreamRes = await fetch(upstreamUrl);
          if (upstreamRes.ok) {
            const fileData = await upstreamRes.arrayBuffer();
            await env.MY_BUCKET.put(key, fileData, {
              httpMetadata: {
                contentType: "image/gif",
                cacheControl: "public, max-age=31536000, immutable",
              },
            });
            object = request.method === "HEAD"
              ? await env.MY_BUCKET.head(key)
              : await env.MY_BUCKET.get(key);
          }
        } catch {
          // 容错放行走 404
        }
      }

      if (!object) {
        return new Response("Image Not Found", { status: 404, headers: corsHeaders });
      }

      const headers = new Headers(corsHeaders);
      object.writeHttpMetadata(headers);
      headers.set("ETag", object.httpEtag);
      headers.set("Cache-Control", "public, max-age=31536000, immutable");

      return new Response(request.method === "HEAD" ? null : object.body, { headers });
    }

    return new Response("Not Found", { status: 404, headers: corsHeaders });
  },
};
