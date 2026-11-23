import type { ReactNode } from "react";

export const metadata = { title: "陪玩售后核对台" };

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="zh-CN">
      <body style={{ fontFamily: "system-ui, sans-serif", maxWidth: 960, margin: "0 auto", padding: 16 }}>
        <nav style={{ display: "flex", gap: 16, marginBottom: 16 }}>
          <a href="/login">登录</a>
          <a href="/sessions">会话列表</a>
          <a href="/check">单局核对</a>
        </nav>
        {children}
      </body>
    </html>
  );
}