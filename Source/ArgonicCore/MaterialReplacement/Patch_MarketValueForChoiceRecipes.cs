using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MaterialReplacement.Defs;
using RimWorld;
using Verse;

namespace MaterialReplacement
{
    // Interchangeability turns a fixed recipe ingredient into a multi-def choice. RimWorld prices a product with no
    // explicit MarketValue from its recipe only while every slot is fixed: StatWorker_MarketValue.CalculableRecipe
    // asks IngredientCount.IsFixedIngredient, CalculatedBaseMarketValue then reads IngredientCount.FixedIngredient. So
    // e.g. Combat Extended ammo drops to ~0 once an added material joins a slot. Redirect those two reads, inside those
    // two vanilla methods only, to answers that treat "original material + added materials" as fixed on the original;
    // vanilla's own arithmetic runs unchanged. Bills, work givers and the ingredient-filter UI are untouched. Call-site
    // rewrite rather than getter patches: the JIT inlines the tiny getters, so a patch on them is skipped. Fixes CE #4263.
    [HarmonyPatch]
    public static class Patch_MarketValueForChoiceRecipes
    {
        private static HashSet<ThingDef> added;

        // Materials interchangeability ADDS to slots (MaterialReplacementDef.replaceWith).
        private static HashSet<ThingDef> Added => added ??= new HashSet<ThingDef>(
            DefDatabase<MaterialReplacementDef>.AllDefsListForReading.Select(m => m.replaceWith).Where(m => m != null));

        // The slot's materials minus the added ones; the original only when exactly one remains (0 or 2+ = vanilla's business).
        private static ThingDef Original(IngredientCount slot)
        {
            List<ThingDef> rest = slot.filter.AllowedThingDefs.Except(Added).ToList();
            return rest.Count == 1 ? rest[0] : null;
        }

        public static bool IsFixedForPricing(IngredientCount slot) => slot.IsFixedIngredient || Original(slot) != null;

        public static ThingDef FixedForPricing(IngredientCount slot) => slot.IsFixedIngredient ? slot.FixedIngredient : Original(slot) ?? slot.FixedIngredient;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculableRecipe));
            yield return AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculatedBaseMarketValue));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> RedirectSlotReads(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo isFixed = AccessTools.PropertyGetter(typeof(IngredientCount), nameof(IngredientCount.IsFixedIngredient));
            MethodInfo fixedIngredient = AccessTools.PropertyGetter(typeof(IngredientCount), nameof(IngredientCount.FixedIngredient));
            int redirected = 0;
            foreach (CodeInstruction ci in instructions)
            {
                // same stack shape: the IngredientCount receiver becomes the static helper's argument
                if (ci.Calls(isFixed)) { ci.opcode = System.Reflection.Emit.OpCodes.Call; ci.operand = AccessTools.Method(typeof(Patch_MarketValueForChoiceRecipes), nameof(IsFixedForPricing)); redirected++; }
                else if (ci.Calls(fixedIngredient)) { ci.opcode = System.Reflection.Emit.OpCodes.Call; ci.operand = AccessTools.Method(typeof(Patch_MarketValueForChoiceRecipes), nameof(FixedForPricing)); redirected++; }
                yield return ci;
            }
            if (redirected == 0) Log.Warning("[MaterialReplacement] found no IngredientCount getter calls to redirect in " + original.Name + "; choice-recipe products keep vanilla pricing");
        }
    }
}
