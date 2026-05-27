const DAY_COLORS = {
    Monday: "bg-orange-50 border-orange-200",
    Tuesday: "bg-amber-50 border-amber-200",
    Wednesday: "bg-yellow-50 border-yellow-200",
    Thursday: "bg-lime-50 border-lime-200",
    Friday: "bg-teal-50 border-teal-200",
    Saturday: "bg-sky-50 border-sky-200",
    Sunday: "bg-violet-50 border-violet-200",
};

const PROTEIN_EMOJI = {
    poultry: "🍗",
    beef: "🥩",
    pork: "🥓",
    fish: "🐟",
    vegetarian: "🥦",
};

export default function MealPlanView({ plan, onSwapDay }) {
    if (!plan?.days) return null;
    // Derive the label from the actual slots in the plan
    const slotNames = plan.days[0]?.slots?.map(s => s.slotName) ?? ["dinner"];
    const slotsLabel = slotNames.length === 3
        ? "full day plan"
        : slotNames.join(" & ") + " plan";

    return (
        <div className="mt-3 max-w-2xl">
            <div className="text-xs text-stone-400 mb-2 flex items-center gap-2">
                <span>7-day {slotsLabel}</span>
                <span className="text-stone-300">·</span>
                <span className="font-mono text-stone-300">{plan.planId?.slice(0, 8)}</span>
            </div>

            <div className="grid grid-cols-1 gap-2">
                {plan.days.map((dayPlan) => {
                    const colorClass = DAY_COLORS[dayPlan.day] ?? "bg-stone-50 border-stone-200";

                    return (
                        <div key={dayPlan.day} className={`border rounded-xl px-4 py-3 ${colorClass}`}>
                            {/* Day label only — no swap at day level */}
                            <div className="text-xs font-semibold text-stone-500 uppercase tracking-wide mb-2">
                                {dayPlan.day}
                            </div>

                            {/* All slots — each gets its own swap button */}
                            <div className="space-y-1.5">
                                {dayPlan.slots?.map((slot) => {
                                    const recipe = slot.recipe;
                                    const dietary = slot.dietaryValidation;
                                    const proteinEmoji = PROTEIN_EMOJI[slot.proteinCategory] ?? "🍽️";
                                    const isIncompatible = dietary && !dietary.isCompatible;

                                    return (
                                        <div key={slot.slotName} className="flex items-center gap-2">
                                            {/* Slot label */}
                                            <div className="w-20 shrink-0 text-xs text-stone-400 capitalize">
                                                {slot.slotName}
                                            </div>

                                            {/* Protein emoji */}
                                            <div className="text-sm shrink-0">{proteinEmoji}</div>

                                            {/* Recipe info */}
                                            <div className="flex-1 min-w-0">
                                                <div className="text-sm font-medium text-stone-700 truncate">
                                                    {recipe.title}
                                                </div>
                                                <div className="flex items-center gap-2">
                                                    {slot.cuisineTag && (
                                                        <span className="text-xs text-stone-400 capitalize">{slot.cuisineTag}</span>
                                                    )}
                                                    {recipe.stepCount && (
                                                        <span className="text-xs text-stone-400">{recipe.stepCount} steps</span>
                                                    )}
                                                    {isIncompatible && (
                                                        <span className="text-xs text-red-500 font-medium">
                                                            ⚠ {dietary.violations?.[0]?.category ?? "dietary issue"}
                                                        </span>
                                                    )}
                                                    {dietary?.isCompatible && (
                                                        <span className="text-xs text-green-600">✓</span>
                                                    )}
                                                </div>
                                            </div>

                                            {/* Per-slot swap button */}
                                            <button
                                                onClick={() => onSwapDay(dayPlan.day, slot.slotName)}
                                                className="shrink-0 text-xs text-stone-400 hover:text-orange-500 hover:bg-orange-50 border border-stone-200 hover:border-orange-200 rounded-lg px-2.5 py-1.5 transition-colors"
                                            >
                                                swap
                                            </button>
                                        </div>
                                    );
                                })}
                            </div>
                        </div>
                    );
                })}
            </div>
        </div>
    );
}