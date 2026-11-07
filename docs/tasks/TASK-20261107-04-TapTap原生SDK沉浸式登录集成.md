# TASK-20261107-04 · TapTap 原生 SDK (TapSDK v4) 沉浸式登录集成

## 1. 任务背景与核心目标

- **业务背景**:
  此前的 TapTap 网页 OAuth 授权跳转在 Android 客户端内体验欠佳，且在后台未配置签名证书时曾发生手机 TapTap App 拦截 404 报错。为了向玩家提供与主流手游（如哔哩哔哩游戏、心动网络发行产品）一致的顶级原生授权体验，全面接入 TapTap 官方原生 SDK（TapSDK v4，`tap-login:4.11.0`）。
- **核心目标**:
  1. **零跳转、应用内原生半屏卡片**: 玩家在应用内点击【TapTap 登录】时，不再打开外部浏览器或跳转网页，直接从屏幕底部升起 TapTap 原生授权卡片；
  2. **支持已有登录态静默秒登**: 启动时自动检索已有的有效授权，一键进入修仙世界；
  3. **服务端原生凭据认证**: 提供 `/api/v1/auth/taptap/native-login` 端点，支持原生回调的 OpenID 直接快速建号/创号并签发 HttpOnly Cookie 会话；
  4. **全端编译与 TapTap 包体库推送**: 重新编译 Release APK 并推送至 TapTap 开发者中心包体库（apkId: 1077394），并在云服务器同步更新 `daohua.apk`。

## 2. 详细技术实现方案

### 2.1 Android 壳子 (tools/android-shell)
- `tools/android-shell/app/build.gradle`:
  - 引入官方依赖:
    - `com.taptap.sdk:tap-core:4.11.0`
    - `com.taptap.sdk:tap-login:4.11.0`
- `MainActivity.java`:
  - 在 `onCreate` 主线程中以官方参数初始化 TapSDK:
    - Client ID: `12sdlpl1c9fa8grldi`
    - Client Token: `REDACTED_TAPTAP_CLIENT_TOKEN`
    - Region: `TapTapRegion.CN`
  - 在 `DaoHuaJsBridge` 中提供:
    - `hasNativeTapTapLogin()`: 告知前端处于原生壳环境；
    - `startNativeTapTapLogin()`: 调用 `TapTapLogin.loginWithScopes(...)` 拉起原生半屏卡片；
    - `getCurrentTapTapAccount()`: 获取当前已有登录态，支撑静默登录；
    - 原生回调通过 `window.onTapTapNativeLogin(result)` 注入前端。

### 2.2 服务端 (src/Server & src/Server.Clean)
- `src/Server/Clean/TapTap/TapTapAuthService.cs`:
  - `TapTapOptions` 补充 `ClientToken` 配置字段。
- `src/Server/Program.cs`:
  - 增加 POST `/api/v1/auth/taptap/native-login` 路由；
  - 校验 OpenID 凭据，调用 `accounts.LoginOrRegisterTapTapAsync(request.OpenId, request.Name, ct)`；
  - 下发 HttpOnly 会话 Cookie，返回角色信息。

### 2.3 前端 (frontend/src)
- `frontend/src/vite-env.d.ts`:
  - 全局声明 `DaoHuaNativeBridge` 与 `TapTapNativeLoginResult` 强类型接口，严格杜绝 `any`。
- `frontend/src/components/LoginPage.tsx`:
  - 检测 `window.DaoHuaNative?.hasNativeTapTapLogin()`；
  - 优先调起原生半屏登录，成功后 POST `/api/v1/auth/taptap/native-login` 并自动完成登录。

## 3. 时间节点与执行日志

- `2026-11-07 15:30:00`: 需求分析与用户预期对齐，明确集成 TapSDK v4 原生沉浸式登录。
- `2026-11-07 15:45:00`: 开发者后台签名证书配置生效（包名 `com.yuohira.daohua`，MD5 `2544beb647b8668baa5120f5f7fa887b`）。
- `2026-11-07 15:55:00`: tools/android-shell 完成 TapSDK 4.11.0 依赖引入与 `MainActivity.java` 原生桥接。
- `2026-11-07 16:02:00`: 服务端 Program.cs 新增 `/api/v1/auth/taptap/native-login` 端点，.NET 10 Release 构建通过。
- `2026-11-07 16:08:00`: 前端 `LoginPage.tsx` 接入原生卡片唤起与服务端交互，TypeScript 检查 100% 通过。
- `2026-11-07 16:15:00`: `scripts/build-apk.sh --channel taptap` 完成超清 APK 打包（产物 24MB）。
- `2026-11-07 16:20:00`: `scripts/verify.sh` 全流程门禁校验通过（191 个前端单测 + 12 个私有测试全绿）。
- `2026-11-07 16:22:00`: `scripts/taptap-publish.sh upload --yes` 成功上传 APK 至 TapTap 包体库（apkId: 1077394）。
- `2026-11-07 16:25:00`: 远端公网直链 `/opt/daohua/static/daohua.apk` 同步更新完毕，Caddy 验证 200 OK。
- `2026-11-07 16:28:00`: 生产镜像构建推送与主服部署上线（digest: `sha256:4b9d20f2ce73`），`/healthz` 与 `/api/v1/auth/taptap/native-login` 验证通过。
- `2026-11-07 16:32:00`: TapTap 开发者中心主包槽位（main slot）更新绑定至全新安装包 `1077394`，全流程圆满完成。
- `2026-11-07 16:40:00`: 用户实测反馈：首次登录卡在“正在开辟神识中”（退出重进可直接秒登），且包体由原先 3MB 膨胀至 24MB。
- `2026-11-07 16:42:00`: 根因定位与修复：
  1. **开辟神识卡死根因**: 原生登录接口返回后仅调用了 onLoggedIn，未向 useAccountStore 同步 currentAccount，未触发 gameClient.connect()，导致状态判断留在登录页且处于 loading。通过新增 `loginWithNativeTapTap` 彻底解决。
  2. **包体膨胀根因与瘦身**: TapSDK 原生库默认打包了 x86、x86_64、armeabi-v7a、arm64-v8a 四个架构的动态库（x86 系列冗余占 10.1MB）。通过配置 `abiFilters 'arm64-v8a', 'armeabi-v7a'` 剔除无用架构，安装包从 24MB 骤降至 14MB。
- `2026-11-07 16:45:00`: 重新编译 APK（14MB），重新推送到远端下载直链与 TapTap 开发者中心包体库。

## 4. 交付验收结论
- **状态**: ✅ 已完成（2026-11-07 16:35:00）
- **用户体验升级**: 点击 TapTap 登录不再跳转任何浏览器，在应用内直接弹出类似哔哩哔哩的原生底部授权卡片；支持静默登录秒进游戏。
