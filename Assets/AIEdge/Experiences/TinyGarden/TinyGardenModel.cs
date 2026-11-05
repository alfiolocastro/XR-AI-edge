namespace AIEdge.TinyGarden
{
    /// <summary>
    /// Prompt and tool schema for the FunctionGemma 270M Tiny Garden fine-tune
    /// (litert-community/functiongemma-270m-ft-tiny-garden), kept identical to Google AI Edge Gallery
    /// so the model sees what it was trained on. Gallery: customtasks/tinygarden/TinyGardenTask.kt, TinyGardenTools.kt.
    /// The model file, sampling and KV-cache size live in <see cref="ModelCatalog.TinyGarden"/>.
    /// </summary>
    public static class TinyGardenModel
    {
        // Verbatim from the Gallery, including the doubled quotes of its Kotlin raw string.
        public const string SystemPrompt =
@"You are an assistant helping the user play a game about gardening.

The environment is a 3x3 grid of garden plots. The plots are numbered 1 through 9.

**Garden Plot Layout**:

- Row 1: Plots 1, 2, 3 (top row)
- Row 2: Plots 4, 5, 6 (middle row)
- Row 3: Plots 7, 8, 9 (bottom row)

Help the user plant seeds, water plots, and harvest flowers.

There are 4 kinds of seeds you can plant:

1. sunflower
2. daisy
3. rose
4. special (edge gallery, special, secret)

Plot Array: For each action, identify all individual plot numbers (1-9) or implied plots (e.g., 'top row' -> 1, 2, 3) and collect them into the `plots` list.

Tips:

- """"top row"""" has plots 1, 2, 3.
- """"middle row"""" has plots 4, 5, 6.
- """"bottom row"""" has plots 7, 8, 9.
- """"left column"""" has plots 1, 4, 7.
- """"middle column"""" has plots 2, 5, 8.
- """"right column"""" has plots 3, 6, 9.
";

        /// <summary>
        /// OpenAPI descriptions matching what LiteRT-LM generates from the Gallery's Kotlin ToolSet
        /// (camelCase function names converted to snake_case).
        /// </summary>
        public const string ToolsJson =
@"[
 {""name"":""water_plots"",""description"":""Water one or more garden plots."",""parameters"":{""type"":""object"",""properties"":{""plots"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""The IDs of the plots to water.""}},""required"":[""plots""]}},
 {""name"":""plant_seed"",""description"":""Plant a seed in one or more garden plots."",""parameters"":{""type"":""object"",""properties"":{""seed"":{""type"":""string"",""description"":""The name of the seed to plant.""},""plots"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""The IDs of the plots to plant a seed in.""}},""required"":[""seed"",""plots""]}},
 {""name"":""harvest_plots"",""description"":""Harvest one or more garden plots."",""parameters"":{""type"":""object"",""properties"":{""plots"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""The IDs of the plots to harvest.""}},""required"":[""plots""]}}
]";
    }
}
