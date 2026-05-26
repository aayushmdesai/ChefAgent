import { useState } from "react";

export default function RecipeCard({ recipe, dietary }) {
  const [expanded, setExpanded] = useState(false);

  if (!recipe) return null;

  const isCompatible = dietary?.isCompatible;
  const isSkipped = !dietary?.isCompatible && dietary?.violations?.length === 0;
  const hasViolations = dietary?.violations?.length > 0;

  // Badge config
  const badge = dietary == null
    ? null
    : isCompatible
    ? { label: "✓ Compatible", className: "bg-green-100 text-green-700 border-green-200" }
    : isSkipped
    ? { label: "⚠ Unverified", className: "bg-yellow-100 text-yellow-700 border-yellow-200" }
    : { label: "✗ Incompatible", className: "bg-red-100 text-red-700 border-red-200" };

  return (
    <div className={`bg-white border rounded-xl overflow-hidden shadow-sm transition-all ${
      isCompatible ? "border-green-200" : hasViolations ? "border-red-100" : "border-stone-200"
    }`}>
      {/* Header */}
      <div
        className="px-4 py-3 cursor-pointer hover:bg-stone-50 transition-colors"
        onClick={() => setExpanded(!expanded)}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="flex-1 min-w-0">
            <h3 className="font-medium text-stone-800 text-sm truncate">
              {recipe.title}
            </h3>
            <p className="text-xs text-stone-400 mt-0.5">
              {recipe.ingredientCount} ingredients · {recipe.stepCount} steps
              {recipe.relevanceScore && (
                <span className="ml-2">· {(recipe.relevanceScore * 100).toFixed(0)}% match</span>
              )}
            </p>
          </div>

          <div className="flex items-center gap-2 shrink-0">
            {badge && (
              <span className={`text-xs px-2 py-0.5 rounded-full border font-medium ${badge.className}`}>
                {badge.label}
              </span>
            )}
            <svg
              className={`w-4 h-4 text-stone-400 transition-transform ${expanded ? "rotate-180" : ""}`}
              fill="none" stroke="currentColor" viewBox="0 0 24 24"
            >
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
            </svg>
          </div>
        </div>
      </div>

      {/* Expanded content */}
      {expanded && (
        <div className="border-t border-stone-100 px-4 py-3 space-y-3">
          {/* Ingredients */}
          <div>
            <h4 className="text-xs font-semibold text-stone-500 uppercase tracking-wide mb-1.5">
              Ingredients
            </h4>
            <ul className="text-xs text-stone-600 space-y-0.5">
              {recipe.ingredients?.map((ing, i) => {
                const violation = dietary?.violations?.find(
                  (v) => v.ingredient === ing
                );
                return (
                  <li key={i} className={`flex gap-1.5 items-start ${violation ? "text-red-600" : ""}`}>
                    <span className="mt-0.5 shrink-0">{violation ? "✗" : "·"}</span>
                    <span>{ing}</span>
                    {violation && (
                      <span className="text-red-400 text-xs">({violation.category})</span>
                    )}
                  </li>
                );
              })}
            </ul>
          </div>

          {/* Directions */}
          <div>
            <h4 className="text-xs font-semibold text-stone-500 uppercase tracking-wide mb-1.5">
              Directions
            </h4>
            <ol className="text-xs text-stone-600 space-y-1 list-decimal list-inside">
              {recipe.directions?.map((step, i) => (
                <li key={i}>{step}</li>
              ))}
            </ol>
          </div>

          {/* Violations + substitutions */}
          {hasViolations && (
            <div className="bg-red-50 rounded-lg p-3 space-y-2">
              <h4 className="text-xs font-semibold text-red-600 uppercase tracking-wide">
                Dietary Issues
              </h4>
              {dietary.violations.map((v, i) => {
                const sub = dietary.substitutions?.find(
                  (s) => s.originalIngredient === v.ingredient
                );
                return (
                  <div key={i} className="text-xs">
                    <span className="text-red-700 font-medium">{v.ingredient}</span>
                    <span className="text-red-400"> · {v.category}</span>
                    {sub && (
                      <div className="text-green-700 mt-0.5">
                        → {sub.suggestedReplacement}
                        {sub.reason && (
                          <span className="text-stone-400"> · {sub.reason}</span>
                        )}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}

          {/* Skipped explanation */}
          {isSkipped && dietary?.explanation && (
            <div className="bg-yellow-50 rounded-lg p-3">
              <p className="text-xs text-yellow-700">{dietary.explanation}</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}