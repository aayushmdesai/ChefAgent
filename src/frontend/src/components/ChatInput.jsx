import { useState } from "react";

export default function ChatInput({ onSend, loading }) {
  const [value, setValue] = useState("");

  const handleSubmit = () => {
    if (!value.trim() || loading) return;
    onSend(value.trim());
    setValue("");
  };

  const handleKey = (e) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      handleSubmit();
    }
  };

  const suggestions = [
    "find me pasta dinner",
    "can I eat pad thai if allergic to gluten?",
    "what is a roux?",
    "plan my meals for the week",
  ];

  return (
    <div className="bg-white border-t border-stone-200 p-4">
      {/* Quick suggestions */}
      <div className="flex gap-2 mb-3 flex-wrap">
        {suggestions.map((s) => (
          <button
            key={s}
            onClick={() => onSend(s)}
            disabled={loading}
            className="text-xs text-stone-500 border border-stone-200 rounded-full px-3 py-1 hover:bg-stone-50 hover:border-stone-300 transition-colors disabled:opacity-40"
          >
            {s}
          </button>
        ))}
      </div>

      {/* Input row */}
      <div className="flex gap-2">
        <input
          type="text"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={handleKey}
          placeholder="Ask me to find a recipe, check your diet, or ask a cooking question..."
          disabled={loading}
          className="flex-1 border border-stone-200 rounded-lg px-4 py-2.5 text-sm text-stone-800 placeholder-stone-400 focus:outline-none focus:ring-2 focus:ring-orange-400 focus:border-transparent disabled:opacity-50"
        />
        <button
          onClick={handleSubmit}
          disabled={!value.trim() || loading}
          className="bg-orange-500 text-white px-5 py-2.5 rounded-lg text-sm font-medium hover:bg-orange-600 transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
        >
          {loading ? "..." : "Send"}
        </button>
      </div>
    </div>
  );
}