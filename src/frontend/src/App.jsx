import { useState, useRef } from "react";
import ChatInput from "./components/ChatInput";
import ChatMessages from "./components/ChatMessages";
import ProfileSidebar from "./components/ProfileSidebar";
function getApiUrl() {
  const hostname = window.location.hostname;

  if (hostname === 'localhost' || hostname === '127.0.0.1') {
    return 'http://localhost:5100/chat';
  }

  const url = `${window.location.protocol}//${hostname.replace('-5173', '-5100')}/chat`;
  console.log('API URL:', url);  // ← add this
  return url;
}

const API_URL = getApiUrl();
// Generate a stable session ID for this browser session
function getSessionId() {
  let id = sessionStorage.getItem("chefagent-session");
  if (!id) {
    id = crypto.randomUUID();
    sessionStorage.setItem("chefagent-session", id);
  }
  return id;
}

export default function App() {
  const sessionId = useRef(getSessionId());
  const [messages, setMessages] = useState([
    {
      id: 1,
      role: "assistant",
      text: "Hi! I'm ChefAgent. Ask me to find recipes, check if a dish fits your diet, plan your week's dinners, or ask a cooking question.",
      recipes: [],
    },
  ]);
  const [loading, setLoading] = useState(false);
  const [profile, setProfile] = useState({ restrictions: [], allergies: [] });

  const sendMessage = async (text) => {
    if (!text.trim() || loading) return;

    // Add user message
    const userMsg = { id: Date.now(), role: "user", text };
    setMessages((prev) => [...prev, userMsg]);
    setLoading(true);

    try {
      const res = await fetch(API_URL, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          message: text,
          sessionId: sessionId.current,
          dietaryProfile:
            profile.restrictions.length > 0 || profile.allergies.length > 0
              ? profile
              : null,
        }),
      });

      const data = await res.json();

      setMessages((prev) => [
        ...prev,
        {
          id: Date.now() + 1,
          role: "assistant",
          text: data.message,
          recipes: data.recipes ?? [],
          mealPlan: data.mealPlan ?? null,        // ← store meal plan
          intent: data.detectedIntent,
          metadata: data.metadata,
        },
      ]);
    } catch (err) {
      console.error("API error:", err);
      setMessages((prev) => [
        ...prev,
        {
          id: Date.now() + 1,
          role: "assistant",
          text: `Error: ${err.message}`,
          recipes: [],
        },
      ]);
    } finally {
      setLoading(false);
    }
  };

  // Swap handler — called from MealPlanView swap button
  const swapDay = (day, slot = "dinner") => {
    sendMessage(`swap ${day} ${slot}`);
  };

  return (
    <div className="flex h-screen bg-stone-50 font-sans">
      {/* Sidebar */}
      <ProfileSidebar profile={profile} setProfile={setProfile} />

      {/* Main chat area */}
      <div className="flex flex-col flex-1 min-w-0">
        {/* Header */}
        <header className="bg-white border-b border-stone-200 px-6 py-4 flex items-center gap-3">
          <div className="w-8 h-8 bg-orange-500 rounded-lg flex items-center justify-center text-white font-bold text-sm">
            CA
          </div>
          <div>
            <h1 className="font-semibold text-stone-800 text-sm">ChefAgent</h1>
            <p className="text-xs text-stone-400">
              Recipe search · Dietary validation · Meal planning
            </p>
          </div>
          {profile.restrictions.length > 0 || profile.allergies.length > 0 ? (
            <div className="ml-auto flex gap-1 flex-wrap justify-end">
              {[...profile.restrictions, ...profile.allergies].map((tag) => (
                <span key={tag} className="text-xs bg-orange-100 text-orange-700 px-2 py-0.5 rounded-full">
                  {tag}
                </span>
              ))}
            </div>
          ) : null}
        </header>

        {/* Messages */}
        <ChatMessages messages={messages} loading={loading} onSwapDay={swapDay} />

        {/* Input */}
        <ChatInput onSend={sendMessage} loading={loading} />
      </div>
    </div>
  );
}