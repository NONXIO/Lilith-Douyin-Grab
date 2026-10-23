# Selective upstream compatibility merge

## Source

源项目：[apebyte/dy-barrage-grab](https://gitee.com/apebyte/dy-barrage-grab)。固定来源：[ce23bde71e51344b4533d5564a85e49ae939cd32](https://gitee.com/apebyte/dy-barrage-grab/commit/ce23bde71e51344b4533d5564a85e49ae939cd32)，2026-04-09，版本说明 2.8.0。

## Ported behavior

- 直播伴侣开播/下播路径兼容尾部斜杠，增加 anchor_finish_info 下播识别。
- PausePop 脚本规则，保留旧 island。
- 低开销 autoplay 页面替换；不再注入全局 WebSocket.close 禁用和轮询暂停脚本。
- 单目标响应处理，不再对同一响应 Task.WhenAll。

## Local behavior retained

- SetSessionRoomId/CheckRoomId 授权与已有 Cloud 工作、握手、A256GCM、本地类型编号及 JSON 字段。
- 跨读取/分片 decoder、独立代理事件线程、扩展弹幕类型和礼物增量。
- JSON 配置与 disableScriptCache，启用时兼容相对资源路径。
- 本地依赖版本不回退到上游版本；不替换 SystemProxy/WsBarrageServer。

## Validation and limitations

离线入口：Framework Debug build 后运行 `powershell -ExecutionPolicy Bypass -File scripts/test-capture-regression.ps1`。
保护产物入口：`DanmakuBackend.exe --capture-self-test`，不启动授权、证书、代理或直播伴侣。
具体验收结果维护在主程序 `openspec/changes/merge-upstream-companion-compatibility/verification.md`。
真实伴侣与物理音频未验收前不得宣称爆音已解决；P1–P3 任务仍开放。
