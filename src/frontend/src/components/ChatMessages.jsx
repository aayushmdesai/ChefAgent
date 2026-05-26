import { useEffect, useRef } from "react";
import RecipeCard from "./RecipeCard";

export default function ChatMessages({ messages, loading }) {
  const bottomRef = useRef(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, loading]);

  return (
    <div className="flex-1 overflow-y-auto px-6 py-4 space-y-4">
      {messages.map((msg) => (
        <div key={msg.id}>
          {/* Message bubble */}
          <div
            className={`flex ${msg.role === "user" ? "justify-end" : "justify-start"}`}
          >
            <div
              className={`max-w-2xl rounded-2xl px-4 py-3 text-sm leading-relaxed ${
                msg.role === "user"
                  ? "bg-orange-500 text-white rounded-br-sm"
                  : "bg-white border border-stone-200 text-stone-700 rounded-bl-sm shadow-sm"
              }`}
            >
              {msg.text}

              {/* Intent + metadata badge */}
              {msg.intent && (
                <div className="mt-2 flex gap-2 flex-wrap">
                  <span className="text-xs opacity-60 bg-black/10 rounded px-1.5 py-0.5">
                    {msg.intent}
                  </span>
                  {msg.metadata?.intentClassifiedBy && (
                    <span className="text-xs opacity-60 bg-black/10 rounded px-1.5 py-0.5">
                      {msg.metadata.intentClassifiedBy}
                    </span>
                  )}
                  {msg.metadata?.dietaryValidationApplied && (
                    <span className="text-xs opacity-60 bg-black/10 rounded px-1.5 py-0.5">
                      diet validated · {msg.metadata.compatibleCount ?? 0} compatible
                    </span>
                  )}
                </div>
              )}
            </div>
          </div>

          {/* Recipe cards */}
          {msg.recipes && msg.recipes.length > 0 && (
            <div className="mt-3 space-y-2 max-w-2xl">
              {msg.recipes.map((item) => (
                <RecipeCard
                  key={item.recipe?.id ?? Math.random()}
                  recipe={item.recipe}
                  dietary={item.dietary}
                />
              ))}
            </div>
          )}
        </div>
      ))}

      {/* Loading indicator */}
      {loading && (
        <div className="flex justify-start">
          <div className="bg-white border border-stone-200 rounded-2xl rounded-bl-sm px-4 py-3 shadow-sm">
            <div className="flex gap-1 items-center">
              <div className="w-1.5 h-1.5 bg-stone-400 rounded-full animate-bounce" style={{ animationDelay: "0ms" }} />
              <div className="w-1.5 h-1.5 bg-stone-400 rounded-full animate-bounce" style={{ animationDelay: "150ms" }} />
              <div className="w-1.5 h-1.5 bg-stone-400 rounded-full animate-bounce" style={{ animationDelay: "300ms" }} />
            </div>
          </div>
        </div>
      )}

      <div ref={bottomRef} />
    </div>
  );
}