"use client";
import { useState } from "react";
import { clearToken, setToken } from "@/lib/api";

export default function LoginPage() {
  const [jwt, setJwt] = useState("");
  const [msg, setMsg] = useState("");
  return (
    <main>
      <h1>登录</h1>
      <p>粘贴 JWT（仅保存在当前标签页 sessionStorage）。</p>
      <textarea rows={6} style={{ width: "100%" }} value={jwt} onChange={(e) => setJwt(e.target.value)} />
      <div style={{ display: "flex", gap: 8, marginTop: 8 }}>
        <button
          onClick={() => {
            const t = jwt.trim().replace(/^Bearer\s+/i, "");
            if (!t) return setMsg("请输入 JWT");
            setToken(t);
            window.location.href = "/sessions";
          }}
        >保存</button>
        <button onClick={() => { clearToken(); setJwt(""); setMsg("已清除"); }}>清除</button>
      </div>
      {msg && <p>{msg}</p>}
    </main>
  );
}