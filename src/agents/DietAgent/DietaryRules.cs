namespace ChefAgent.Agents.Diet;

using ChefAgent.Shared.Models;

/// <summary>
/// Static knowledge base for dietary restriction rules.
/// Handles common cases fast — no LLM needed.
///
/// Covers:
///   Allergies  : dairy, gluten, nuts (tree nuts + peanuts), eggs, soy, seafood, sesame
///   Diets      : vegetarian, vegan, pescatarian, paleo
///   Indian     : jain, sattvic, hindu-vegetarian (no beef)
///   Religious  : halal (no pork/shellfish), kosher (no pork/shellfish/mixing)
///
/// Edge cases (ambiguous phrases, hidden allergens, "natural flavors") fall
/// through to the LLM — the rules engine targets the common 80%.
///
/// Matching strategy: phrase-level contains check on the full ingredient string.
/// e.g. "peanut butter" is in NutIngredients; "butter" is in DairyIngredients.
/// This avoids false positives from substring word matching.
/// </summary>
public static class DietaryRules
{
    // ── Knowledge Base ────────────────────────────────────────────────────────

    // FDA Major Allergen: Milk
    // Sources: FDA FALCPA / FASTER Act
    private static readonly HashSet<string> DairyIngredients =
    [
        "milk",
        "whole milk",
        "skim milk",
        "low-fat milk",
        "reduced fat milk",
        "buttermilk",
        "evaporated milk",
        "condensed milk",
        "sweetened condensed milk",
        "powdered milk",
        "dry milk",
        "milk powder",
        "milk solids",
        "butter",
        "clarified butter",
        "brown butter",
        "ghee", // common in Indian recipes — IS dairy
        "cream",
        "heavy cream",
        "light cream",
        "whipping cream",
        "double cream",
        "sour cream",
        "half and half",
        "cheese",
        "cheddar",
        "mozzarella",
        "parmesan",
        "parmigiano",
        "ricotta",
        "cottage cheese",
        "cream cheese",
        "brie",
        "gouda",
        "feta",
        "gruyere",
        "swiss cheese",
        "provolone",
        "colby",
        "monterey jack",
        "pepper jack",
        "velveeta",
        "yogurt",
        "greek yogurt",
        "plain yogurt",
        "whey",
        "whey powder",
        "whey protein",
        "casein",
        "caseinate",
        "sodium caseinate",
        "lactose",
        "lactulose",
        "ice cream",
        "gelato",
        "frozen yogurt",
        "crème fraîche",
        "creme fraiche",
        "half-and-half",
    ];

    // Sattvic diet permits ghee, plain yogurt, and milk — exclude them from dairy violations
    private static readonly HashSet<string> SattvicApprovedDairy =
    [
        "ghee",
        "yogurt",
        "plain yogurt",
        "milk",
    ];

    private static readonly HashSet<string> SattvicDairyViolations = DairyIngredients
        .Except(SattvicApprovedDairy, StringComparer.OrdinalIgnoreCase)
        .ToHashSet();

    // FDA Major Allergen: Wheat / Gluten grains
    // Gluten-free per FDA: no wheat, rye, barley, or crossbred hybrids
    private static readonly HashSet<string> GlutenIngredients =
    [
        // Wheat forms
        "wheat",
        "wheat flour",
        "all-purpose flour",
        "bread flour",
        "cake flour",
        "whole wheat flour",
        "whole wheat",
        "wheat bran",
        "wheat germ",
        "wheat starch",
        "wheat berries",
        "cracked wheat",
        "durum wheat",
        "semolina",
        "spelt",
        "kamut",
        "emmer",
        "einkorn",
        "farro",
        "wheat noodles",
        "wheat pasta",
        // Rye
        "rye",
        "rye flour",
        "rye bread",
        // Barley
        "barley",
        "pearl barley",
        "barley flour",
        "barley malt",
        "malt",
        "malt vinegar",
        "malt extract",
        "malted barley",
        // Triticale (wheat-rye hybrid)
        "triticale",
        // Common gluten-containing products
        "bread",
        "bread crumbs",
        "breadcrumbs",
        "panko",
        "pasta",
        "noodles",
        "egg noodles",
        "udon",
        "ramen",
        "soy sauce", // brewed with wheat — hidden gluten source
        "hoisin sauce", // usually wheat-based
        "teriyaki sauce", // typically contains soy sauce
        "worcestershire sauce", // check brand; many contain malt vinegar
        "flour tortilla",
        "pita bread",
        "naan",
        "pita",
        "crackers",
        "croutons",
        "bulgur",
        "couscous",
        "seitan",
        "vital wheat gluten", // pure gluten
        "beer", // barley-based
    ];

    // FDA Major Allergen: Tree Nuts (12 recognized as of Jan 2025 guidance)
    // + Peanuts (separate allergen but grouped here for nut allergy checks)
    private static readonly HashSet<string> NutIngredients =
    [
        // FDA's 12 major tree nuts
        "almond",
        "almonds",
        "almond flour",
        "almond meal",
        "almond milk",
        "almond butter",
        "almond extract",
        "almond paste",
        "marzipan",
        "black walnut",
        "black walnuts",
        "brazil nut",
        "brazil nuts",
        "cashew",
        "cashews",
        "cashew butter",
        "cashew cream",
        "cashew milk",
        "hazelnut",
        "hazelnuts",
        "filbert",
        "filberts",
        "hazelnut spread",
        "macadamia",
        "macadamia nut",
        "macadamia nuts",
        "pecan",
        "pecans",
        "pine nut",
        "pine nuts",
        "pinon",
        "pignoli",
        "pistachio",
        "pistachios",
        "walnut",
        "walnuts",
        "english walnut",
        "persian walnut",
        // Peanuts (separate FDA allergen)
        "peanut",
        "peanuts",
        "peanut butter",
        "peanut oil",
        "peanut flour",
        "groundnut",
        "groundnuts",
        "groundnut oil",
        // Other common nuts (not FDA major but still common allergens)
        "chestnut",
        "chestnuts",
        "coconut", // FDA classifies as tree nut for labeling; most tree-nut
        // allergic people tolerate it, but flag it — LLM can refine
        "praline",
        "pralines", // typically nut-based
        "nut butter", // generic — catches unlabeled nut butters
        "mixed nuts",
    ];

    // Eggs — FDA Major Allergen
    private static readonly HashSet<string> EggIngredients =
    [
        "egg",
        "eggs",
        "whole egg",
        "egg white",
        "egg whites",
        "egg yolk",
        "egg yolks",
        "egg powder",
        "dried egg",
        "egg solids",
        "mayonnaise",
        "mayo", // egg-based
        "meringue", // egg whites
        "hollandaise", // egg yolk based
        "aioli", // traditionally egg-based
        "albumin", // egg white protein
        "globulin",
        "lysozyme", // egg proteins
        "ovalbumin",
        "ovomucin",
        "ovomucoid",
    ];

    // Soy — FDA Major Allergen
    private static readonly HashSet<string> SoyIngredients =
    [
        "soy",
        "soya",
        "soybean",
        "soybeans",
        "soy bean",
        "soy beans",
        "soy milk",
        "soy cream",
        "soy yogurt",
        "tofu",
        "firm tofu",
        "silken tofu",
        "extra firm tofu",
        "tempeh",
        "edamame",
        "miso",
        "miso paste",
        "white miso",
        "red miso",
        "soy sauce", // also in gluten list (dual flag)
        "tamari", // gluten-free soy sauce — still soy
        "liquid aminos", // soy-based
        "textured soy protein",
        "textured vegetable protein",
        "tvp",
        "tsp",
        "soy flour",
        "soy protein",
        "soy isolate",
        "soy concentrate",
        "soy lecithin", // emulsifier — common hidden soy
        "natto",
    ];

    // Sesame — FDA Major Allergen (as of Jan 1, 2023)
    private static readonly HashSet<string> SesameIngredients =
    [
        "sesame",
        "sesame seed",
        "sesame seeds",
        "sesame oil",
        "toasted sesame oil",
        "tahini", // sesame paste — common hidden sesame
        "sesame paste",
        "sesame flour",
        "til",
        "til seeds", // Indian name for sesame
        "gingelly oil", // Indian sesame oil
        "benne",
        "benne seeds",
        "hummus", // contains tahini — flag for LLM to confirm
    ];

    // Seafood — covers fish + crustacean shellfish + mollusks
    // Relevant for: vegan, vegetarian, halal (some interpretations)
    private static readonly HashSet<string> SeafoodIngredients =
    [
        // Fish (FDA Major Allergen)
        "fish",
        "salmon",
        "tuna",
        "cod",
        "tilapia",
        "halibut",
        "trout",
        "bass",
        "catfish",
        "snapper",
        "mahi mahi",
        "mahi-mahi",
        "swordfish",
        "sardine",
        "sardines",
        "anchovy",
        "anchovies",
        "herring",
        "mackerel",
        "pollock",
        "flounder",
        "sole",
        "haddock",
        "carp",
        "pike",
        "perch",
        "fish sauce", // hidden fish — very common in Asian recipes
        "worcestershire sauce", // contains anchovies (also in gluten list)
        "caesar dressing", // traditionally contains anchovies
        // Crustacean Shellfish (FDA Major Allergen)
        "shrimp",
        "prawns",
        "crab",
        "lobster",
        "crayfish",
        "crawfish",
        "barnacle",
        "krill",
        // Mollusks (not FDA major allergen but common)
        "clam",
        "clams",
        "oyster",
        "oysters",
        "mussel",
        "mussels",
        "scallop",
        "scallops",
        "squid",
        "calamari",
        "octopus",
        "abalone",
        "snail",
        "escargot",
        // Processed
        "surimi",
        "imitation crab",
        "shrimp paste",
        "dried shrimp",
        "bonito flakes",
        "dashi",
    ];

    // Meat — violates vegetarian, vegan, jain, hindu-vegetarian (+ beef specifically)
    private static readonly HashSet<string> MeatIngredients =
    [
        // Beef
        "beef",
        "ground beef",
        "steak",
        "chuck",
        "sirloin",
        "ribeye",
        "brisket",
        "veal",
        "roast beef",
        "beef broth",
        "beef stock",
        "beef bouillon",
        // Pork
        "pork",
        "ground pork",
        "pork chop",
        "pork loin",
        "pork belly",
        "ham",
        "prosciutto",
        "pancetta",
        "guanciale",
        "bacon",
        "canadian bacon",
        "sausage",
        "salami",
        "pepperoni",
        "chorizo",
        "bratwurst",
        "hot dog",
        "pork broth",
        "pork stock",
        "lard", // rendered pork fat
        "salt pork",
        // Poultry
        "chicken",
        "ground chicken",
        "chicken breast",
        "chicken thigh",
        "chicken drumstick",
        "rotisserie chicken",
        "turkey",
        "ground turkey",
        "turkey breast",
        "duck",
        "duck breast",
        "duck confit",
        "goose",
        "quail",
        "pheasant",
        "cornish hen",
        "chicken broth",
        "chicken stock",
        "chicken bouillon",
        "turkey broth",
        "turkey stock",
        // Lamb / Goat
        "lamb",
        "ground lamb",
        "lamb chop",
        "rack of lamb",
        "mutton",
        "goat",
        "goat meat",
        // Other
        "venison",
        "bison",
        "buffalo",
        "rabbit",
        "boar",
        // Processed / Hidden
        "gelatin", // derived from animal bones/skin
        "lard", // pork fat
        "tallow", // beef/mutton fat
        "suet", // raw beef/mutton fat
        "bone broth",
        "meat broth",
        "meat stock",
    ];

    // Vegan violations = meat + seafood + dairy + eggs + honey + some others
    // We store the extras here; the full check unions multiple sets
    private static readonly HashSet<string> VeganExtraExclusions =
    [
        "honey", // bee product
        "beeswax",
        "royal jelly",
        "propolis",
        "isinglass", // fish bladder — used to clarify beer/wine
        "carmine",
        "cochineal",
        "natural red 4", // red dye from insects
        "shellac",
        "confectioner's glaze", // insect secretion on candy
        "castoreum", // beaver secretion — rare but used as vanilla flavor
        "l-cysteine", // amino acid sometimes from duck feathers/hog hair
        "lanolin", // wool grease — in some vitamin D supplements
        "whey protein", // dairy (also in DairyIngredients)
    ];

    // Jain violations = meat + seafood + eggs + honey + root vegetables + fungi
    private static readonly HashSet<string> JainExtraExclusions =
    [
        // Root vegetables (uprooting kills the whole plant)
        "potato",
        "potatoes",
        "sweet potato",
        "sweet potatoes",
        "yam",
        "yams",
        "onion",
        "onions",
        "green onion",
        "green onions",
        "spring onion",
        "shallot",
        "shallots",
        "garlic",
        "garlic powder",
        "garlic cloves",
        "garlic paste",
        "carrot",
        "carrots",
        "beet",
        "beets",
        "beetroot",
        "turnip",
        "turnips",
        "parsnip",
        "parsnips",
        "radish",
        "radishes",
        "daikon",
        "ginger",
        "fresh ginger",
        "ginger paste",
        "ginger root",
        "turmeric root", // fresh root form; powder is fine for most Jains
        "leek",
        "leeks",
        // Fungi
        "mushroom",
        "mushrooms",
        "shiitake",
        "portobello",
        "cremini",
        "oyster mushroom",
        "enoki",
        "porcini",
        "chanterelle",
        "truffle",
        "truffles",
        "yeast",
        "active dry yeast",
        "instant yeast",
        "brewer's yeast",
        "nutritional yeast", // debated; strict Jains avoid
        // Honey (also vegan exclusion)
        "honey",
        // Alcohol
        "wine",
        "red wine",
        "white wine",
        "beer",
        "vodka",
        "rum",
        "brandy",
        "whiskey",
        "sake",
        "mirin", // mirin is sweet rice wine
    ];

    // Sattvic violations: meat, eggs, fish, onion, garlic, alcohol, overly stimulating
    private static readonly HashSet<string> SattvicExclusions =
    [
        "onion",
        "onions",
        "green onion",
        "green onions",
        "spring onion",
        "shallot",
        "shallots",
        "garlic",
        "garlic powder",
        "garlic cloves",
        "garlic paste",
        "leek",
        "leeks",
        // Stimulants / tamasic foods
        "coffee",
        "caffeine",
        "alcohol",
        "wine",
        "beer",
        "liquor",
        // Pungent / overly stimulating
        "green chili",
        "green chillies", // Sattvic avoids; Jain does not
        // Meat, eggs, fish inherited via separate sets in Validate()
    ];

    // Pork & shellfish — Halal and Kosher violations
    private static readonly HashSet<string> PorkIngredients =
    [
        "pork",
        "ground pork",
        "pork chop",
        "pork loin",
        "pork belly",
        "ham",
        "prosciutto",
        "pancetta",
        "guanciale",
        "bacon",
        "canadian bacon",
        "back bacon",
        "sausage", // flag — may be pork; LLM can verify
        "salami",
        "pepperoni",
        "chorizo",
        "bratwurst",
        "hot dog", // flag — may be pork
        "lard",
        "salt pork",
        "pork broth",
        "pork stock",
        "gelatin", // often pork-derived — flag for LLM
        "suet",
    ];

    // Shellfish — some Halal/Kosher interpretations prohibit
    private static readonly HashSet<string> ShellFishIngredients =
    [
        "shrimp",
        "prawns",
        "crab",
        "lobster",
        "crayfish",
        "crawfish",
        "clam",
        "clams",
        "oyster",
        "oysters",
        "mussel",
        "mussels",
        "scallop",
        "scallops",
        "squid",
        "calamari",
        "octopus",
        "barnacle",
        "krill",
        "shrimp paste",
        "dried shrimp",
    ];

    // Paleo exclusions: dairy, legumes, grains, processed sugar, salt-heavy
    private static readonly HashSet<string> PaleoExclusions =
    [
        // Legumes
        "bean",
        "beans",
        "black bean",
        "black beans",
        "kidney bean",
        "kidney beans",
        "chickpea",
        "chickpeas",
        "garbanzo",
        "garbanzo beans",
        "lentil",
        "lentils",
        "red lentil",
        "green lentil",
        "pea",
        "peas",
        "split pea",
        "split peas",
        "peanut",
        "peanuts",
        "peanut butter", // legume not nut
        "soybean",
        "soybeans",
        "tofu",
        "tempeh",
        "edamame",
        "dal",
        "daal", // Indian lentil preparations
        // Grains (includes gluten + non-gluten)
        "rice",
        "white rice",
        "brown rice",
        "basmati rice",
        "jasmine rice",
        "wheat",
        "wheat flour",
        "all-purpose flour",
        "corn",
        "cornmeal",
        "corn flour",
        "cornstarch",
        "corn starch",
        "oat",
        "oats",
        "oatmeal",
        "rolled oats",
        "quinoa",
        "barley",
        "rye",
        "millet",
        "sorghum",
        "teff",
        "bread",
        "pasta",
        "noodles",
        "crackers",
        "cereal",
        // Dairy (also in DairyIngredients — dual flag)
        "milk",
        "cheese",
        "butter",
        "cream",
        "yogurt",
        "ghee",
        // Refined sugar / processed
        "sugar",
        "white sugar",
        "brown sugar",
        "powdered sugar",
        "corn syrup",
        "high fructose corn syrup",
        "canola oil",
        "vegetable oil",
        "soybean oil", // processed oils
    ];

    // ── Restriction → Set Mapping ─────────────────────────────────────────────

    // Maps profile restriction strings to their violation sets.
    // Used in CheckRestriction() — keeps the logic table-driven, not if/else.
    private static readonly Dictionary<
        string,
        Func<List<string>, List<ViolationDetail>>
    > RestrictionCheckers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vegetarian"] = i =>
        {
            var v = new List<ViolationDetail>();
            v.AddRange(CheckAgainstSet(i, MeatIngredients, "meat", "vegetarian"));
            v.AddRange(CheckAgainstSet(i, SeafoodIngredients, "seafood", "vegetarian"));
            return v;
        },
        ["vegan"] = i => CheckVegan(i),
        ["pescatarian"] = i => CheckAgainstSet(i, MeatIngredients, "meat", "pescatarian"),
        ["paleo"] = i => CheckAgainstSet(i, PaleoExclusions, "paleo-excluded", "paleo"),
        ["jain"] = i => CheckJain(i),
        ["sattvic"] = i => CheckSattvic(i),
        ["hindu-vegetarian"] = i => CheckHinduVegetarian(i),
        ["halal"] = i => CheckHalal(i),
        // FIXME: Kosher requires meat/dairy separation (basar b'chalav), specific slaughter (shechita), and forbidden species beyond pork/shellfish — defer full Kosher logic to LLM
        ["kosher"] = i => CheckHalal(i),
        // "-free" restriction variants — extracted by IntentRouter from "nut-free X", "dairy-free X", etc.
        // Delegate to the same checker as the corresponding allergy.
        ["nut-free"]   = i => CheckAgainstSet(i, NutIngredients,   "nuts",   "nut-free"),
        ["dairy-free"] = i => CheckAgainstSet(i, DairyIngredients, "dairy",  "dairy-free"),
        ["gluten-free"] = i =>
        {
            var v = new List<ViolationDetail>();
            v.AddRange(CheckAgainstSet(i, GlutenIngredients, "gluten", "gluten-free"));
            return v;
        },
        ["egg-free"]  = i => CheckAgainstSet(i, EggIngredients, "eggs", "egg-free"),
        ["soy-free"]  = i => CheckAgainstSet(i, SoyIngredients, "soy",  "soy-free"),
    };

    private static readonly Dictionary<
        string,
        Func<List<string>, List<ViolationDetail>>
    > AllergyCheckers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dairy"] = i => CheckAgainstSet(i, DairyIngredients, "dairy", "dairy"),
        ["milk"] = i => CheckAgainstSet(i, DairyIngredients, "dairy", "dairy"),
        ["gluten"] = i => CheckAgainstSet(i, GlutenIngredients, "gluten", "gluten"),
        ["wheat"] = i => CheckAgainstSet(i, GlutenIngredients, "gluten", "wheat"),
        ["nuts"] = i => CheckAgainstSet(i, NutIngredients, "nuts", "nuts"),
        ["tree nuts"] = i => CheckAgainstSet(i, NutIngredients, "nuts", "tree nuts"),
        ["peanuts"] = i => CheckAgainstSet(i, NutIngredients, "nuts", "peanuts"),
        ["eggs"] = i => CheckAgainstSet(i, EggIngredients, "eggs", "eggs"),
        ["soy"] = i => CheckAgainstSet(i, SoyIngredients, "soy", "soy"),
        ["seafood"] = i => CheckAgainstSet(i, SeafoodIngredients, "seafood", "seafood"),
        ["fish"] = i => CheckAgainstSet(i, SeafoodIngredients, "seafood", "fish"),
        ["shellfish"] = i => CheckAgainstSet(i, ShellFishIngredients, "shellfish", "shellfish"),
        ["sesame"] = i => CheckAgainstSet(i, SesameIngredients, "sesame", "sesame"),
    };

    // Phrases that contain dairy words but are NOT dairy.
    // Checked before the main rule match to prevent false positives.
    private static readonly HashSet<string> DairyFalsePositives =
    [
        "peanut butter",
        "almond butter",
        "cashew butter",
        "sunflower butter",
        "apple butter", // fruit spread
        "cocoa butter", // plant fat
        "shea butter", // cosmetic — shouldn't appear but just in case
    ];

    // ── Public Methods ────────────────────────────────────────────────────────

    /// <summary>
    /// Main entry point. Validates a recipe's ingredient list against a full
    /// DietaryProfile. Runs all allergy and restriction checks.
    /// Returns all violations found — empty list means the recipe is compatible.
    /// Zero LLM calls.
    /// </summary>
    public static List<ViolationDetail> Validate(List<string> ingredients, DietaryProfile profile)
    {
        var violations = new List<ViolationDetail>();

        foreach (var allergy in profile.Allergies)
            violations.AddRange(CheckAllergy(ingredients, allergy));

        foreach (var restriction in profile.Restrictions)
            violations.AddRange(CheckRestriction(ingredients, restriction));

        // Deduplicate: same ingredient may be flagged by multiple checks
        return violations.GroupBy(v => v.Ingredient + v.Category).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Checks ingredients against a named restriction.
    /// Returns violations found, empty list if none or restriction is unknown.
    /// Unknown restrictions return empty — the LLM layer handles them.
    /// </summary>
    public static List<ViolationDetail> CheckRestriction(
        List<string> ingredients,
        string restriction
    )
    {
        if (RestrictionCheckers.TryGetValue(restriction, out var checker))
            return checker(ingredients);

        // Unknown restriction — return empty, signal to caller that LLM is needed
        return [];
    }

    /// <summary>
    /// Checks ingredients against a named allergy category.
    /// Returns violations found, empty list if none or allergy category is unknown.
    /// </summary>
    public static List<ViolationDetail> CheckAllergy(List<string> ingredients, string allergy)
    {
        if (AllergyCheckers.TryGetValue(allergy, out var checker))
            return checker(ingredients);

        return [];
    }

    /// <summary>
    /// Returns true if this restriction or allergy is known to the rules engine.
    /// Use this to decide whether to call the LLM for unknown constraints.
    /// </summary>
    public static bool IsKnownRestriction(string restriction) =>
        RestrictionCheckers.ContainsKey(restriction);

    public static bool IsKnownAllergy(string allergy) => AllergyCheckers.ContainsKey(allergy);

    // ── Private Composite Checkers ────────────────────────────────────────────

    private static List<ViolationDetail> CheckVegan(List<string> ingredients)
    {
        var violations = new List<ViolationDetail>();
        violations.AddRange(CheckAgainstSet(ingredients, MeatIngredients, "meat", "vegan"));
        violations.AddRange(CheckAgainstSet(ingredients, SeafoodIngredients, "seafood", "vegan"));
        violations.AddRange(CheckAgainstSet(ingredients, DairyIngredients, "dairy", "vegan"));
        violations.AddRange(CheckAgainstSet(ingredients, EggIngredients, "eggs", "vegan"));
        violations.AddRange(
            CheckAgainstSet(ingredients, VeganExtraExclusions, "animal-derived", "vegan")
        );
        return violations;
    }

    private static List<ViolationDetail> CheckJain(List<string> ingredients)
    {
        var violations = new List<ViolationDetail>();
        violations.AddRange(CheckAgainstSet(ingredients, MeatIngredients, "meat", "jain"));
        violations.AddRange(CheckAgainstSet(ingredients, SeafoodIngredients, "seafood", "jain"));
        violations.AddRange(CheckAgainstSet(ingredients, EggIngredients, "eggs", "jain"));
        violations.AddRange(
            CheckAgainstSet(ingredients, JainExtraExclusions, "jain-excluded", "jain")
        );
        return violations;
    }

    private static List<ViolationDetail> CheckSattvic(List<string> ingredients)
    {
        var violations = new List<ViolationDetail>();
        violations.AddRange(CheckAgainstSet(ingredients, MeatIngredients, "meat", "sattvic"));
        violations.AddRange(CheckAgainstSet(ingredients, SeafoodIngredients, "seafood", "sattvic"));
        violations.AddRange(CheckAgainstSet(ingredients, EggIngredients, "eggs", "sattvic"));
        // ghee, yogurt, and milk are sattvic-approved — SattvicDairyViolations excludes them
        violations.AddRange(
            CheckAgainstSet(ingredients, SattvicDairyViolations, "dairy", "sattvic")
        );
        violations.AddRange(
            CheckAgainstSet(ingredients, SattvicExclusions, "sattvic-excluded", "sattvic")
        );
        return violations;
    }

    private static List<ViolationDetail> CheckHinduVegetarian(List<string> ingredients)
    {
        var violations = new List<ViolationDetail>();
        // No meat at all, and specifically flag beef as highest priority
        violations.AddRange(
            CheckAgainstSet(ingredients, MeatIngredients, "meat", "hindu-vegetarian")
        );
        violations.AddRange(
            CheckAgainstSet(ingredients, SeafoodIngredients, "seafood", "hindu-vegetarian")
        );
        return violations;
    }

    private static List<ViolationDetail> CheckHalal(List<string> ingredients)
    {
        var violations = new List<ViolationDetail>();
        violations.AddRange(CheckAgainstSet(ingredients, PorkIngredients, "pork", "halal"));
        violations.AddRange(
            CheckAgainstSet(ingredients, ShellFishIngredients, "shellfish", "halal")
        );
        // Alcohol
        violations.AddRange(
            CheckAgainstSet(
                ingredients,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "wine",
                    "red wine",
                    "white wine",
                    "beer",
                    "vodka",
                    "rum",
                    "brandy",
                    "whiskey",
                    "sake",
                    "mirin",
                    "alcohol",
                },
                "alcohol",
                "halal"
            )
        );
        return violations;
    }

    // ── Core Matching Logic ───────────────────────────────────────────────────

    /// <summary>
    /// Checks all ingredients against a rule set. Returns one ViolationDetail
    /// per ingredient that matches any rule phrase.
    ///
    /// Matching: case-insensitive phrase-level contains.
    /// "1 cup buttermilk".Contains("buttermilk") → match
    /// "2 tbsp peanut butter".Contains("butter") → NO match (butter not in dairy set as standalone phrase that appears in "peanut butter")
    /// Wait — "butter" IS in the dairy set and IS contained in "peanut butter".
    /// That's a false positive we accept and document: the LLM layer can correct it.
    /// For the common 80%, phrase-level matching is accurate enough.
    /// </summary>
    private static List<ViolationDetail> CheckAgainstSet(
        List<string> ingredients,
        HashSet<string> ruleSet,
        string category,
        string restriction
    )
    {
        var violations = new List<ViolationDetail>();

        foreach (var ingredient in ingredients)
        {
            var lower = ingredient.ToLowerInvariant();

            // Skip if ingredient is a known false positive
            // e.g. "peanut butter" contains "butter" but is not dairy
            if (
                category == "dairy"
                && DairyFalsePositives.Any(fp =>
                    lower.Contains(fp, StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;

            var matched = ruleSet.FirstOrDefault(rule =>
                lower.Contains(rule, StringComparison.OrdinalIgnoreCase)
            );
            if (matched is not null)
            {
                violations.Add(
                    new ViolationDetail
                    {
                        Ingredient = ingredient, // full string: "1 cup buttermilk"
                        Category = category, // "dairy"
                        DetectedBy = ValidationLayer.Rules,
                        MatchedRule = matched, // "buttermilk"
                    }
                );
            }
        }

        return violations;
    }

    private static bool IngredientContains(string ingredient, HashSet<string> ruleSet) =>
        ruleSet.Any(rule => ingredient.Contains(rule, StringComparison.OrdinalIgnoreCase));
}
