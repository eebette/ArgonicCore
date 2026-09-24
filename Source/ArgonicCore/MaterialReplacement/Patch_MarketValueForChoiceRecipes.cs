using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MaterialReplacement.Defs;
using RimWorld;
using UnityEngine;
using Verse;

namespace MaterialReplacement
{
    // Interchangeability turns a fixed recipe ingredient into a multi-def choice; RimWorld's
    // market-value calc only derives a value from an all-fixed recipe (CalculableRecipe), so a
    // product with no explicit MarketValue -- e.g. most Combat Extended ammo -- drops to ~0.
    // Recompute from the recipe, valuing each ingredient by its ORIGINAL (non-replacement)
    // material where the recipe still offers one -- so this framework never lowers a product's
    // market value -- and by the cheapest added material only as a fallback. Fixes CE #4263.
    [HarmonyPatch(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculatedBaseMarketValue))]
    public static class Patch_MarketValueForChoiceRecipes
    {
        private static readonly Dictionary<ThingDef, float> cache = new Dictionary<ThingDef, float>();
        private static HashSet<ThingDef> added;

        // Materials interchangeability ADDS to ingredient filters; used only as a value fallback.
        private static HashSet<ThingDef> Added => added ??= new HashSet<ThingDef>(
            DefDatabase<MaterialReplacementDef>.AllDefsListForReading.Select(m => m.replaceWith).Where(m => m != null));

        static void Postfix(BuildableDef def, ref float __result)
        {
            if (__result > 0.01f || !(def is ThingDef td) || !td.CostList.NullOrEmpty() || td.CostStuffCount > 0) return;
            if (cache.TryGetValue(td, out float cached)) { if (cached >= 0f) __result = cached; return; }

            RecipeDef r = DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(x => x.ProducedThingDef == td);
            if (r == null || r.ingredients.NullOrEmpty()) { cache[td] = -1f; return; }

            float sum = 0f;
            foreach (IngredientCount ing in r.ingredients)
            {
                ThingDef pick = ing.filter.AllowedThingDefs
                    .OrderBy(d => Added.Contains(d) ? 1 : 0)   // original material first
                    .ThenBy(d => d.BaseMarketValue)            // cheapest within the chosen group
                    .FirstOrDefault();
                if (pick == null) { cache[td] = -1f; return; }
                sum += ing.CountRequiredOfFor(pick, r) * pick.BaseMarketValue;
            }
            if (r.workAmount > 2f) sum += r.workAmount * StatWorker_MarketValue.ValuePerWork;
            __result = cache[td] = sum / Mathf.Max(1, r.products[0].count);
        }
    }
}
