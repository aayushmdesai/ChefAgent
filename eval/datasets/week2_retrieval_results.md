# ChefAgent — Week 2 Search Quality Report

**Generated:** 2026-05-25 14:11
**Queries tested:** 24 (12 from Week 1, 12 new in Week 2)

## Category Summary — Week 2 vs Week 1

| Category | Week 1 Avg | Week 2 Avg | Change | Status |
|----------|-----------|-----------|--------|--------|
| By Ingredients | 0.8282 | 0.8282 | -0.0000 | ➡️ |
| Cuisine | 0.6815 | 0.6815 | +0.0000 | ➡️ |
| Dietary | 0.6995 | 0.7172 | +0.0177 | 📈 |
| Exact Match | 0.8053 | 0.8053 | -0.0000 | ➡️ |
| Filtering | — (new) | 0.7360 | — | 🆕 |
| Irrelevant | 0.5822 | 0.5822 | -0.0000 | ➡️ |
| Misspelling | — (new) | 0.6769 | — | 🆕 |
| Multi-Intent | — (new) | 0.7079 | — | 🆕 |
| Negation | — (new) | 0.7314 | — | 🆕 |
| Negation (X-free) | — (new) | 0.7022 | — | 🆕 |
| Situation | 0.6373 | 0.6373 | +0.0000 | ➡️ |
| Technique | — (new) | 0.7552 | — | 🆕 |

## Negation Handling

- **4/4 queries clean** (no excluded ingredients in results)
- Week 1: "pasta without tomatoes" returned "Pasta With Tomatoes" as #2
- Week 2: Negation handler strips excluded terms pre-search, filters violations post-search

## Per-Query Results

### Exact Match

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| chocolate chip cookies | The Best Chocolate Chip Cookies | 0.8078 |  |
| banana bread | Banana Bread | 0.8027 |  |

### By Ingredients

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| what can I make with chicken and rice | Baked Chicken And Rice | 0.8228 |  |
| recipes using eggs cheese and spinach | Spinach Squares | 0.8336 |  |

### Dietary

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| vegetarian pasta dinner | Chicken And Pasta Salad | 0.7099 |  |
| dessert without dairy | Four Layer Dessert | 0.7246 |  |

### Cuisine

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| spicy Mexican dinner | Spicy Glazed Chicken | 0.6679 |  |
| Italian soup | Homemade Soup Mix | 0.6952 |  |

### Situation

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| quick easy weeknight meal | Quick Meat Loaf | 0.6394 |  |
| something warm and comforting for winter | Happiness | 0.6353 |  |

### Irrelevant

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| how to change a car tire | Kart-Wheels | 0.5865 | ✅ Low score (expected) |
| python programming tutorial for beginners | My Friend Joyce'S "Bathtub Casserol | 0.5778 | ✅ Low score (expected) |

### Negation

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| pasta without tomatoes | Homemade Pasta | 0.7115 | ✅ Clean |
| cookies without nuts | Cookies For A Crowd | 0.7513 | ✅ Clean |

### Negation (X-free)

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| gluten-free dessert | Lucy Anne'S Quick Dessert | 0.6948 | ✅ Clean |
| dairy-free soup | Cheese Soup | 0.7096 | ✅ Clean |

### Filtering

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| simple chicken dinner | Easy Chicken Dinner | 0.7737 | ✅ Within filter |
| easy dessert | Lucy Anne'S Quick Dessert | 0.6983 | ✅ Within filter |

### Misspelling

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| chiken noodle soop | Noodle Nibbles | 0.6462 |  |
| lasanga recipe | One Pot Lasagna | 0.7076 |  |

### Technique

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| slow cooker beef stew | Crock-Pot Sweet And Sour Beef And V | 0.7961 |  |
| grilled chicken breast | Grilled Chicken And Fresh Vegetable | 0.7142 |  |

### Multi-Intent

| Query | Top Result | Score | Notes |
|-------|-----------|-------|-------|
| healthy chicken meal under 30 minutes | Dieter'S Delight | 0.7322 |  |
| easy vegetarian dinner for two | Easy Supper | 0.6836 |  |
