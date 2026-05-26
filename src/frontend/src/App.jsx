import { useState } from "react";
import ChatInput from "./components/ChatInput";
import ChatMessages from "./components/ChatMessages";
import ProfileSidebar from "./components/ProfileSidebar";

const API_URL = "http://localhost:5000/chat";

export default function App() {
  const [messages, setMessages] = useState([
    {
      id: 1,
      role: "assistant",
      text: "Hi! I'm ChefAgent. Ask me to find recipes, check if a dish fits your diet, or ask a cooking question.",
      recipes: [],
    },
  ]);
  const [loading, setLoading] = useState(false);
  const [profile, setProfile] = useState({
    restrictions: [],
    allergies: [],
  });

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
          intent: data.detectedIntent,
          metadata: data.metadata,
        },
      ]);
    } catch {
      setMessages((prev) => [
        ...prev,
        {
          id: Date.now() + 1,
          role: "assistant",
          text: "Sorry — couldn't reach the API. Make sure the server is running on localhost:5000.",
          recipes: [],
        },
      ]);
    } finally {
      setLoading(false);
    }
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
              Recipe search · Dietary validation · Month 1
            </p>
          </div>
          {profile.restrictions.length > 0 || profile.allergies.length > 0 ? (
            <div className="ml-auto flex gap-1 flex-wrap justify-end">
              {[...profile.restrictions, ...profile.allergies].map((tag) => (
                <span
                  key={tag}
                  className="text-xs bg-orange-100 text-orange-700 px-2 py-0.5 rounded-full"
                >
                  {tag}
                </span>
              ))}
            </div>
          ) : null}
        </header>

        {/* Messages */}
        <ChatMessages messages={messages} loading={loading} />

        {/* Input */}
        <ChatInput onSend={sendMessage} loading={loading} />
      </div>
    </div>
  );
}