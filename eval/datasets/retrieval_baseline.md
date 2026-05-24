# ChefAgent — Retrieval Evaluation Dataset

Generated: 2026-05-23 22:12
Collection: recipes (10K documents)
Embedding model: nomic-embed-text (768 dim)

## Rating Guide

| Rating | Meaning |
|--------|---------|
| ✅ Relevant | Top result directly answers the query |
| 🟡 Partial | Related but missing key aspects (wrong cuisine, has excluded ingredient) |
| ❌ Bad | Irrelevant or nonsensical result |
| ⬛ Expected Bad | Query is intentionally irrelevant — low score is correct |

---

## Exact Match

### Q1: "chocolate chip cookies"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | The Best Chocolate Chip Cookies | 0.8078 | <!-- YOUR RATING --> |
| 2 | Chocolate Chip Cookies | 0.8036 | <!-- YOUR RATING --> |
| 3 | Chocolate Chip Cookies | 0.8008 | <!-- YOUR RATING --> |

**Top ingredients:** 1/2 tsp. baking soda, 2 1/4 c. flour, 1 c. oleo, 1 c. sugar
**Notes:** <!-- Your observations -->

### Q2: "banana bread"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Banana Bread | 0.8027 | <!-- YOUR RATING --> |
| 2 | Banana Bread | 0.7978 | <!-- YOUR RATING --> |
| 3 | The "Bestest" Banana Bread | 0.7966 | <!-- YOUR RATING --> |

**Top ingredients:** 3 tbsp. shortening, 1 c. sugar, 1 well beaten egg, 1/2 c. sour cream
**Notes:** <!-- Your observations -->

---

## By Ingredients

### Q3: "what can I make with chicken and rice"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Baked Chicken And Rice | 0.8228 | <!-- YOUR RATING --> |
| 2 | Baked Chicken In Rice | 0.8222 | <!-- YOUR RATING --> |
| 3 | Chicken And Rice | 0.8203 | <!-- YOUR RATING --> |

**Top ingredients:** 2 1/2 lb. chicken, 5 c. water, 1 large onion, 1 tsp. salt
**Notes:** <!-- Your observations -->

### Q4: "recipes using eggs cheese and spinach"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Spinach Squares | 0.8336 | <!-- YOUR RATING --> |
| 2 | Spinach Squares | 0.8259 | <!-- YOUR RATING --> |
| 3 | Spinach Squares | 0.8186 | <!-- YOUR RATING --> |

**Top ingredients:** 2 small onions, chopped, 2 cloves garlic, minced, 3 Tbsp. oil, 1 (10 oz.) box frozen chopped spinach
**Notes:** <!-- Your observations -->

---

## Dietary

### Q5: "vegetarian pasta dinner"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Chicken And Pasta Salad | 0.7099 | <!-- YOUR RATING --> |
| 2 | Chicken And Pasta Salad | 0.7040 | <!-- YOUR RATING --> |
| 3 | Seafood Pasta Salad | 0.7036 | <!-- YOUR RATING --> |

**Top ingredients:** 1/2 c. Miracle Whip, 1/4 c. Zesty Italian dressing, 2 Tbsp. Parmesan cheese, 2 c. rotini pasta, cooked and drained
**Notes:** <!-- Your observations -->

### Q6: "dessert without dairy"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Mom'S Dessert | 0.6892 | <!-- YOUR RATING --> |
| 2 | Sugar-Free Cheese Cake | 0.6863 | <!-- YOUR RATING --> |
| 3 | No Sugar Cake | 0.6821 | <!-- YOUR RATING --> |

**Top ingredients:** 1 c. water, 1 c. raisins, 1 c. applesauce, 2 eggs
**Notes:** <!-- Your observations -->

---

## Cuisine

### Q7: "spicy Mexican dinner"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Spicy Glazed Chicken | 0.6679 | <!-- YOUR RATING --> |
| 2 | Spicy Polynesian Beef Or Chicken | 0.6595 | <!-- YOUR RATING --> |
| 3 | "Spicy" Cake | 0.6591 | <!-- YOUR RATING --> |

**Top ingredients:** 4 eggs, 1 3/4 c. brown sugar, salt, 1 Tbsp. allspice
**Notes:** <!-- Your observations -->

### Q8: "Italian soup"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Homemade Soup Mix | 0.6952 | <!-- YOUR RATING --> |
| 2 | Depression Soup | 0.6940 | <!-- YOUR RATING --> |
| 3 | 2 Step Meal | 0.6935 | <!-- YOUR RATING --> |

**Top ingredients:** 4 cans vegetable beef soup, Minute rice
**Notes:** <!-- Your observations -->

---

## Situation

### Q9: "quick easy weeknight meal"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Quick Meat Loaf | 0.6394 | <!-- YOUR RATING --> |
| 2 | Quick One Dish Meal | 0.6332 | <!-- YOUR RATING --> |
| 3 | Easy Supper | 0.6306 | <!-- YOUR RATING --> |

**Top ingredients:** 1 can Spam, ground, enough ground onion to make 1/2 c., 1 small green pepper, ground and sauteed with onion, 1 c. ground bread crumbs
**Notes:** <!-- Your observations -->

### Q10: "something warm and comforting for winter"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Happiness | 0.6353 | <!-- YOUR RATING --> |
| 2 | Good Homemade Frosting | 0.5928 | <!-- YOUR RATING --> |
| 3 | Jan'S Winter Soup | 0.5880 | <!-- YOUR RATING --> |

**Top ingredients:** 2 Tbsp. butter, 2 medium onions, chopped, 1 lb. ground beef or turkey, 1 clove garlic, minced
**Notes:** <!-- Your observations -->

---

## Irrelevant

### Q11: "how to change a car tire"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Kart-Wheels | 0.5865 | <!-- YOUR RATING --> |
| 2 | Rocky Road Fudge | 0.5733 | <!-- YOUR RATING --> |
| 3 | Chili Con Carne | 0.5676 | <!-- YOUR RATING --> |

**Top ingredients:** 3 lb. ground beef, 2 yellow onions, 2 cans tomato sauce, 2 to 3 c. water
**Notes:** <!-- Your observations -->

### Q12: "python programming tutorial for beginners"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | My Friend Joyce'S "Bathtub Casserole" | 0.5778 | <!-- YOUR RATING --> |
| 2 | Pasta With Pink Vodka Sauce | 0.5637 | <!-- YOUR RATING --> |
| 3 | Chicken And Penne Pasta With Kalamata Olives | 0.5630 | <!-- YOUR RATING --> |

**Top ingredients:** about 5 Tbsp. extra virgin olive oil, red pepper flakes, 4 to 6 garlic cloves, chopped or sliced, 3 or 4 boneless chicken breasts
**Notes:** <!-- Your observations -->

---

## Misspelling

### Q13: "chiken noodle soop"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Noodle Nibbles | 0.6462 | <!-- YOUR RATING --> |
| 2 | Lukshen Kugel(Noodle Pudding) | 0.6404 | <!-- YOUR RATING --> |
| 3 | Noodle Nests | 0.6091 | <!-- YOUR RATING --> |

**Top ingredients:** 1 can chocolate frosting, 4 c. chow mein noodles, jelly bean eggs
**Notes:** <!-- Your observations -->

### Q14: "macoroni and cheeze"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Macaroni Cheez | 0.6353 | <!-- YOUR RATING --> |
| 2 | The Lady'S Cheesy Mac | 0.5779 | <!-- YOUR RATING --> |
| 3 | Macaroni And Cheese | 0.5755 | <!-- YOUR RATING --> |

**Top ingredients:** 4 c. cooked macaroni (2 c. uncooked), 2 c. (8 oz.) low fat sharp cheddar cheese, 1 c. 1% low fat cottage cheese, 3/4 c. no fat sour cream
**Notes:** <!-- Your observations -->

---

## Constraint

### Q15: "30 minute dinner for two"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Easy Supper | 0.6461 | <!-- YOUR RATING --> |
| 2 | Complete Dinner Casserole | 0.6340 | <!-- YOUR RATING --> |
| 3 | Vegetable Casserole | 0.6261 | <!-- YOUR RATING --> |

**Top ingredients:** 2 cans mixed vegetables, drained, 1 small jar Cheez Whiz, 1/2 c. mayonnaise, small chopped onion
**Notes:** <!-- Your observations -->

### Q16: "5 ingredient easy lunch"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Friendship Tea | 0.6861 | <!-- YOUR RATING --> |
| 2 | Easy Supper | 0.6706 | <!-- YOUR RATING --> |
| 3 | Easy Salad | 0.6698 | <!-- YOUR RATING --> |

**Top ingredients:** 1 large can crushed pineapple, drained, 1 large can cherry, strawberry or blueberry pie filling, 1 can Eagle Brand condensed milk, 1 (8 oz.) container Cool Whip
**Notes:** <!-- Your observations -->

---

## Negative

### Q17: "pasta without tomatoes"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Pasta Ala Renee | 0.7229 | <!-- YOUR RATING --> |
| 2 | Pasta With Tomatoes And Four Cheeses | 0.7154 | <!-- YOUR RATING --> |
| 3 | Fat-Free Pasta Salad(Use Fat-Free Honey Dijon, Ranch Or Italian Dressing) | 0.6997 | <!-- YOUR RATING --> |

**Top ingredients:** 1 box Ruffles pasta, 1 onion (1/2 c.), 1 small bag frozen peas, 2 cucumbers
**Notes:** <!-- Your observations -->

### Q18: "cookies without eggs"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | No Bake Cookies | 0.7503 | <!-- YOUR RATING --> |
| 2 | No Bake Cookies | 0.7486 | <!-- YOUR RATING --> |
| 3 | No Bake Cookies | 0.7440 | <!-- YOUR RATING --> |

**Top ingredients:** 1 stick oleo, 2 c. sugar, 2 Tbsp. peanut butter, 1/2 c. milk
**Notes:** <!-- Your observations -->

---

## Multi-Intent

### Q19: "healthy breakfast that kids will love"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Friendship Tea | 0.7048 | <!-- YOUR RATING --> |
| 2 | Jim'S Breakfast Delight | 0.6350 | <!-- YOUR RATING --> |
| 3 | Papa Lander'S Easy Pancakes | 0.6328 | <!-- YOUR RATING --> |

**Top ingredients:** cinnamon buns, maple syrup
**Notes:** <!-- Your observations -->

### Q20: "cheap high protein meal prep"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Corn Meal Mush(A High-Protein Alternative To Pancakes.) | 0.6249 | <!-- YOUR RATING --> |
| 2 | Low-Fat Texas Trash | 0.6159 | <!-- YOUR RATING --> |
| 3 | Scramble | 0.6124 | <!-- YOUR RATING --> |

**Top ingredients:** 2 lb. salted mixed nuts, 1 (11 oz.) box spoon size Shredded Wheat, 1 (10 oz.) pkg. Cheerios, 1 (6 oz.) pkg. Rice Chex
**Notes:** <!-- Your observations -->

---

## Conversational

### Q21: "I'm bored of eating the same thing"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Lazy Day Salad | 0.5435 | <!-- YOUR RATING --> |
| 2 | Friendship Tea | 0.5395 | <!-- YOUR RATING --> |
| 3 | Delight(4 Layered Dessert) | 0.5339 | <!-- YOUR RATING --> |

**Top ingredients:** 1 c. flour, 1 c. chopped pecans, 1 stick butter, 1 (8 oz.) pkg. cream cheese
**Notes:** <!-- Your observations -->

### Q22: "what should I bring to a potluck"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Potluck Potatoes | 0.6769 | <!-- YOUR RATING --> |
| 2 | Pot Roast | 0.6661 | <!-- YOUR RATING --> |
| 3 | Potluck Baked Beans | 0.6645 | <!-- YOUR RATING --> |

**Top ingredients:** 1/2 lb. diced bacon, 1 lb. ground beef, 1 chopped onion, 16 oz. can kidney beans, drained
**Notes:** <!-- Your observations -->

---

## Technique

### Q23: "slow cooker beef stew"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Crock-Pot Sweet And Sour Beef And Vegetables | 0.7961 | <!-- YOUR RATING --> |
| 2 | Slowly Deviled Beef | 0.7938 | <!-- YOUR RATING --> |
| 3 | Slow Cooked Baked Stew | 0.7888 | <!-- YOUR RATING --> |

**Top ingredients:** 2 lb. lean beef round, cut into bite size pieces, 2 c. chopped onion, 2 c. quartered potatoes, 2 c. carrot chunks
**Notes:** <!-- Your observations -->

### Q24: "grilled fish with lemon"

| Rank | Title | Score | Rating |
|------|-------|-------|--------|
| 1 | Flavorful Fish | 0.7545 | <!-- YOUR RATING --> |
| 2 | Speedy Lemon Sauce | 0.7544 | <!-- YOUR RATING --> |
| 3 | Grilled Lemon Pepper Shrimp | 0.7367 | <!-- YOUR RATING --> |

**Top ingredients:** 1 to 2 lb. medium or large shrimp, peeled and deveined, lemon pepper seasoning, 1/2 lb. bacon, 1/2 c. lemon juice
**Notes:** <!-- Your observations -->

---

## Summary

| Category | Queries | Avg Top Score | Overall Rating |
|----------|---------|---------------|----------------|
| Exact Match | 2 | 0.8053 | <!-- RATE --> |
| By Ingredients | 2 | 0.8282 | <!-- RATE --> |
| Dietary | 2 | 0.6995 | <!-- RATE --> |
| Cuisine | 2 | 0.6815 | <!-- RATE --> |
| Situation | 2 | 0.6373 | <!-- RATE --> |
| Irrelevant | 2 | 0.5822 | <!-- RATE --> |
| Misspelling | 2 | 0.6408 | <!-- RATE --> |
| Constraint | 2 | 0.6661 | <!-- RATE --> |
| Negative | 2 | 0.7366 | <!-- RATE --> |
| Multi-Intent | 2 | 0.6649 | <!-- RATE --> |
| Conversational | 2 | 0.6102 | <!-- RATE --> |
| Technique | 2 | 0.7753 | <!-- RATE --> |

**Month 1 baseline — revisit with RAGAS eval in Month 3**
