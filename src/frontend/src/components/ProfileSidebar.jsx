const RESTRICTIONS = [
  { label: "Vegetarian", value: "vegetarian" },
  { label: "Vegan", value: "vegan" },
  { label: "Pescatarian", value: "pescatarian" },
  { label: "Gluten-free", value: "gluten-free" },
  { label: "Dairy-free", value: "dairy-free" },
  { label: "Paleo", value: "paleo" },
  { label: "Jain", value: "jain" },
  { label: "Sattvic", value: "sattvic" },
  { label: "Halal", value: "halal" },
  { label: "Kosher", value: "kosher" },
];

const ALLERGIES = [
  { label: "Nuts", value: "nuts" },
  { label: "Dairy", value: "dairy" },
  { label: "Gluten", value: "gluten" },
  { label: "Eggs", value: "eggs" },
  { label: "Soy", value: "soy" },
  { label: "Seafood", value: "seafood" },
  { label: "Sesame", value: "sesame" },
];

export default function ProfileSidebar({ profile, setProfile }) {
  const toggle = (type, value) => {
    setProfile((prev) => {
      const current = prev[type];
      const next = current.includes(value)
        ? current.filter((v) => v !== value)
        : [...current, value];
      return { ...prev, [type]: next };
    });
  };

  const clearAll = () => setProfile({ restrictions: [], allergies: [] });

  const hasAny =
    profile.restrictions.length > 0 || profile.allergies.length > 0;

  return (
    <aside className="w-56 bg-white border-r border-stone-200 flex flex-col shrink-0">
      {/* Header */}
      <div className="px-4 py-4 border-b border-stone-100">
        <div className="flex items-center justify-between">
          <h2 className="text-xs font-semibold text-stone-600 uppercase tracking-wide">
            Dietary Profile
          </h2>
          {hasAny && (
            <button
              onClick={clearAll}
              className="text-xs text-stone-400 hover:text-red-500 transition-colors"
            >
              Clear
            </button>
          )}
        </div>
        <p className="text-xs text-stone-400 mt-1">
          Sent with every request
        </p>
      </div>

      <div className="flex-1 overflow-y-auto px-4 py-3 space-y-4">
        {/* Restrictions */}
        <div>
          <h3 className="text-xs font-semibold text-stone-500 uppercase tracking-wide mb-2">
            Restrictions
          </h3>
          <div className="space-y-1.5">
            {RESTRICTIONS.map(({ label, value }) => {
              const active = profile.restrictions.includes(value);
              return (
                <button
                  key={value}
                  onClick={() => toggle("restrictions", value)}
                  className={`w-full text-left text-xs px-3 py-1.5 rounded-lg transition-all ${
                    active
                      ? "bg-orange-500 text-white font-medium"
                      : "text-stone-600 hover:bg-stone-50"
                  }`}
                >
                  {label}
                </button>
              );
            })}
          </div>
        </div>

        {/* Allergies */}
        <div>
          <h3 className="text-xs font-semibold text-stone-500 uppercase tracking-wide mb-2">
            Allergies
          </h3>
          <div className="space-y-1.5">
            {ALLERGIES.map(({ label, value }) => {
              const active = profile.allergies.includes(value);
              return (
                <button
                  key={value}
                  onClick={() => toggle("allergies", value)}
                  className={`w-full text-left text-xs px-3 py-1.5 rounded-lg transition-all ${
                    active
                      ? "bg-red-500 text-white font-medium"
                      : "text-stone-600 hover:bg-stone-50"
                  }`}
                >
                  {label}
                </button>
              );
            })}
          </div>
        </div>
      </div>

      {/* Active summary */}
      {hasAny && (
        <div className="border-t border-stone-100 px-4 py-3">
          <p className="text-xs text-stone-400 mb-1">Active profile:</p>
          <div className="flex flex-wrap gap-1">
            {profile.restrictions.map((r) => (
              <span key={r} className="text-xs bg-orange-100 text-orange-700 rounded px-1.5 py-0.5">
                {r}
              </span>
            ))}
            {profile.allergies.map((a) => (
              <span key={a} className="text-xs bg-red-100 text-red-700 rounded px-1.5 py-0.5">
                {a} allergy
              </span>
            ))}
          </div>
        </div>
      )}
    </aside>
  );
}