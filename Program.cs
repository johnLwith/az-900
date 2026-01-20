using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Default
{
    internal static class Program
    {
        // Defaults
        private static readonly string[] DefaultInputTxtFiles = ["data/1.txt", "data/2.txt"];
        private const string DefaultFailedAnkiJson = "az900-anki-failed.json";
        private const string DefaultDeckName = "az-900";
        private const string DefaultModelName = "Basic";
        private const string DefaultDbPath = "az900.db";

        public static async Task Main(string[] args)
        {
            // ---------------------------
            // CLI: modes = ai | anki | all
            // ---------------------------
            // Examples:
            // - dotnet run -- ai   --out outdir          --db az900.db   --limit 20   (saves outdir/az900-questions-20.json and writes to db)
            // - dotnet run -- anki --db az900.db         --deck az-900
            // - dotnet run -- all  --out outdir          --db az900.db   --deck az-900 (runs ai then anki)

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Information)
                    .AddSimpleConsole(o =>
                    {
                        o.SingleLine = true;
                        o.TimestampFormat = "HH:mm:ss ";
                    });
            });
            var logger = loggerFactory.CreateLogger("az-900");

            var opts = ParseArgs(args);

            if (opts.ShowHelp || opts.Mode is not ("ai" or "anki" or "all"))
            {
                PrintUsage(logger);
                return;
            }

            if (opts.Mode is "ai" or "all")
            {
                await RunAiModeAsync(opts, DefaultInputTxtFiles, logger);
            }

            if (opts.Mode is "anki" or "all")
            {
                await RunAnkiModeAsync(opts, logger);
            }
        }

        private static async Task RunAiModeAsync(CliOptions opts, IEnumerable<string> inputTxtFiles, ILogger logger)
        {
            var questions = LoadQuestionsFromTxt(inputTxtFiles);
            logger.LogInformation("Loaded questions: {Count}", questions.Count);

            var repo = new Data.QuestionRepository(opts.DbPath);
            await repo.EnsureCreatedAsync();

            var aiSettings = AISettings.Load("appsettings.json");
            IAIProvider aiProvider = AIProviderFactory.Create(aiSettings);

            var processed = await RunAIStageAsync(questions, aiProvider, repo, opts.Limit, logger);
            if (!string.IsNullOrWhiteSpace(opts.OutputJson))
            {
                var outputPath = ResolveOutputPath(opts.OutputJson, processed);
                SaveQuestionsAsJson(outputPath, questions);
                logger.LogInformation("Saved AI results to: {OutputPath}", outputPath);
            }
            else
            {
                logger.LogInformation("AI results stored in SQLite only (no JSON export requested).");
            }
        }

        private static async Task RunAnkiModeAsync(CliOptions opts, ILogger logger)
        {
            var repo = new Data.QuestionRepository(opts.DbPath);
            await repo.EnsureCreatedAsync();

            List<Question> questions;
            if (!string.IsNullOrWhiteSpace(opts.InputJson))
            {
                questions = LoadQuestionsFromJson(opts.InputJson);
                logger.LogInformation("Loaded from JSON: {Count}", questions.Count);
            }
            else
            {
                questions = await repo.GetAllAsync(onlyWithAI: true);
                logger.LogInformation("Loaded from DB: {Count}", questions.Count);
            }

            var questionsWithAI = questions.Where(HasAnyAI).ToList();
            logger.LogInformation("Questions with AI responses: {Count}", questionsWithAI.Count);

            if (questionsWithAI.Count == 0)
            {
                logger.LogInformation("No questions with AI responses to sync to Anki.");
                return;
            }

            try
            {
                await Anki.AddToAnkiAsync(questionsWithAI, deckName: opts.DeckName, modelName: opts.ModelName);
                logger.LogInformation("Notes pushed to Anki successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to push notes to Anki");
                Anki.SaveAsJson(questionsWithAI, DefaultFailedAnkiJson, deckName: opts.DeckName, modelName: opts.ModelName);
                logger.LogInformation("Saved notes to '{File}' for manual import into Anki.", DefaultFailedAnkiJson);
            }
        }

        private static bool HasAnyAI(Question q) => !string.IsNullOrWhiteSpace(q.AIRaw) || q.AIParsed != null;

        private static CliOptions ParseArgs(string[] args)
        {
            var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "all";

            var inputJson = string.Empty;
            var outputJson = string.Empty;
            var deckName = DefaultDeckName;
            var modelName = DefaultModelName;
            var dbPath = DefaultDbPath;
            int? limit = null;
            var showHelp = mode is "--help" or "-h" or "help";

            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a is "--help" or "-h")
                {
                    showHelp = true;
                }
                else if (a is "--in" or "-i")
                {
                    if (i + 1 < args.Length) inputJson = args[++i];
                }
                else if (a is "--out" or "-o")
                {
                    if (i + 1 < args.Length) outputJson = args[++i];
                }
                else if (a is "--deck")
                {
                    if (i + 1 < args.Length) deckName = args[++i];
                }
                else if (a is "--model")
                {
                    if (i + 1 < args.Length) modelName = args[++i];
                }
                else if (a is "--db")
                {
                    if (i + 1 < args.Length) dbPath = args[++i];
                }
                else if (a is "--limit")
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var n)) limit = n;
                }
            }

            // If user invoked `dotnet run -- --help` then mode is "--help" and we show usage.
            if (showHelp) mode = "help";

            return new CliOptions(mode, inputJson, outputJson, deckName, modelName, limit, showHelp, dbPath);
        }

        private static void PrintUsage(ILogger logger)
        {
            logger.LogInformation("Usage:");
            logger.LogInformation("  dotnet run -- ai   [--out <folder-or-file>] [--db <path>] [--limit <n>]");
            logger.LogInformation("  dotnet run -- anki [--in <file>]  [--db <path>] [--deck <name>] [--model <name>]");
            logger.LogInformation("  dotnet run -- all  [--in <file>] [--out <folder-or-file>] [--db <path>] [--limit <n>] [--deck <name>] [--model <name>]");
            logger.LogInformation("");
            logger.LogInformation("Defaults:");
            logger.LogInformation("  --in/--out: (optional, uses DB if not specified)");
            logger.LogInformation("  --db: {Default}", DefaultDbPath);
            logger.LogInformation("  --deck: {Default}", DefaultDeckName);
            logger.LogInformation("  --model: {Default}", DefaultModelName);
        }

        private static List<Question> LoadQuestionsFromTxt(IEnumerable<string> files)
        {
            var questions = new List<Question>();

            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);

                Question? question = null;
                foreach (var line in lines)
                {
                    if (Regex.IsMatch(line, @"(?=Topic\s+\d+\s*Question\s+#\d+)", RegexOptions.Singleline))
                    {
                        question = new Question(line);
                        questions.Add(question);
                    }
                    else if (question != null)
                    {
                        question.Raw += line + Environment.NewLine;
                    }
                }
            }

            return questions;
        }

        private static List<Question> LoadQuestionsFromJson(string filePath)
        {
            if (!File.Exists(filePath))
                return new List<Question>();

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<Question>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<Question>();
        }

        private static void SaveQuestionsAsJson(string filePath, List<Question> questions)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(questions, options);
            File.WriteAllText(filePath, json);
        }

        private static async Task<int> RunAIStageAsync(
            List<Question> questions,
            IAIProvider aiProvider,
            Data.QuestionRepository repo,
            int? limit,
            ILogger logger)
        {
            int processedCount = 0;
            int skippedCount = 0;
            int totalCount = questions.Count;

            foreach (var question in questions)
            {
                if (limit.HasValue && processedCount >= limit.Value)
                    break;

                // Check if question already has AI results in database
                if (await repo.HasAIAsync(question.Header))
                {
                    skippedCount++;
                    logger.LogInformation(
                        "Skipped {Skipped}/{Total} (already has AI results): {Header}",
                        skippedCount,
                        totalCount,
                        question.Header
                    );
                    continue;
                }

                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    question.AIRaw = await aiProvider.RunAsync(question.Raw);
                    question.AIParsed = TryParseAI(question.AIRaw);
                    if (question.AIParsed != null)
                    {
                        question.AIQuestion = question.AIParsed.Question;
                        question.AIOptions = question.AIParsed.Options;
                        question.AICorrectAnswer = question.AIParsed.CorrectAnswer;
                        question.AICorrectAnswerText = question.AIParsed.CorrectAnswerText;
                        question.AITopic = question.AIParsed.Topic;
                        question.AIExplanation = question.AIParsed.Explanation;
                        question.AINotes = question.AIParsed.Notes;
                    }
                    stopwatch.Stop();
                    processedCount++;
                    logger.LogInformation(
                        "AI processed {Processed}/{Total} (skipped {Skipped}) in {ElapsedMs} ms",
                        processedCount,
                        totalCount,
                        skippedCount,
                        stopwatch.ElapsedMilliseconds
                    );

                    // Persist immediately
                    await repo.UpsertAsync(question);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AI failed at {Index}/{Total}", processedCount + skippedCount + 1, totalCount);
                }
            }

            return processedCount;
        }

        private static AIParsed? TryParseAI(string aiText)
        {
            if (string.IsNullOrWhiteSpace(aiText)) return null;

            // Handle markdown fenced blocks if the model returns ```json ... ```
            var jsonText = aiText.Trim();
            if (jsonText.Contains("```json", StringComparison.OrdinalIgnoreCase))
            {
                var start = jsonText.IndexOf("```json", StringComparison.OrdinalIgnoreCase) + "```json".Length;
                var end = jsonText.IndexOf("```", start, StringComparison.OrdinalIgnoreCase);
                if (end > start) jsonText = jsonText.Substring(start, end - start).Trim();
            }
            else if (jsonText.Contains("```", StringComparison.OrdinalIgnoreCase))
            {
                var start = jsonText.IndexOf("```", StringComparison.OrdinalIgnoreCase) + "```".Length;
                var end = jsonText.IndexOf("```", start, StringComparison.OrdinalIgnoreCase);
                if (end > start) jsonText = jsonText.Substring(start, end - start).Trim();
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                static string? GetString(JsonElement el, string name)
                    => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                Dictionary<string, string>? options = null;
                if (root.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Object)
                {
                    options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in optEl.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                            options[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                List<string>? notes = null;
                if (root.TryGetProperty("notes", out var notesEl) && notesEl.ValueKind == JsonValueKind.Array)
                {
                    notes = new List<string>();
                    foreach (var n in notesEl.EnumerateArray())
                    {
                        if (n.ValueKind == JsonValueKind.String) notes.Add(n.GetString() ?? "");
                    }
                }

                return new AIParsed
                {
                    Question = GetString(root, "question"),
                    Options = options,
                    CorrectAnswer = GetString(root, "correctAnswer"),
                    CorrectAnswerText = GetString(root, "correctAnswerText"),
                    Topic = GetString(root, "topic"),
                    Explanation = GetString(root, "explanation"),
                    Notes = notes
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveOutputPath(string outArg, int processedCount)
        {
            // If the user passes a folder (existing or no extension), create a file inside that folder
            var target = outArg;

            var looksLikeFile = Path.HasExtension(target) || target.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeFile)
            {
                Directory.CreateDirectory(target);
                var fileName = $"az900-questions-{Math.Max(1, processedCount)}.json";
                return Path.Combine(target, fileName);
            }

            // Ensure parent directory exists for explicit file paths
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            return target;
        }
    }

    internal record CliOptions(
        string Mode,
        string InputJson,
        string OutputJson,
        string DeckName,
        string ModelName,
        int? Limit,
        bool ShowHelp,
        string DbPath
    );

    /// <summary>
    /// Represents a parsed exam question plus AI-generated metadata.
    /// </summary>
    public record Question(string Header)
    {
        public string Raw { get; set; } = "";
        public string AIRaw { get; set; } = "";

        // Expanded/structured AI fields (parsed from AI JSON)
        public AIParsed? AIParsed { get; set; }
        public string? AIQuestion { get; set; }
        public Dictionary<string, string>? AIOptions { get; set; }
        public string? AICorrectAnswer { get; set; }
        public string? AICorrectAnswerText { get; set; }
        public string? AITopic { get; set; }
        public string? AIExplanation { get; set; }
        public List<string>? AINotes { get; set; }
    }

    /// <summary>
    /// Structured representation of the AI JSON output for a question.
    /// </summary>
    public class AIParsed
    {
        public string? Question { get; set; }
        public Dictionary<string, string>? Options { get; set; }
        public string? CorrectAnswer { get; set; }
        public string? CorrectAnswerText { get; set; }
        public string? Topic { get; set; }
        public string? Explanation { get; set; }
        public List<string>? Notes { get; set; }
    }

    internal interface IAIProvider
    {
        Task<string> RunAsync(string content);
    }

    internal sealed class SKProvider : IAIProvider
    {
        public async Task<string> RunAsync(string content) => await SK.RunAsync(content);
    }

    internal static class AIProviderFactory
    {
        public static IAIProvider Create(AISettings settings)
        {
            _ = settings;
            return new SKProvider();
        }
    }

    internal record AISettings
    {
        public static AISettings Load(string path)
        {
            // Settings loading kept for future extensibility, but currently always uses SK.
            _ = path;
            return new AISettings();
        }
    }

    public static class SK
    {
        private static readonly Kernel Kernel;
        private static readonly IChatCompletionService ChatService;

        static SK()
        {
            Kernel = Microsoft.SemanticKernel.Kernel.CreateBuilder()
                .AddOpenAIChatCompletion(
                    modelId: "qwen3-coder-30b-a3b-instruct-mlx",
                    apiKey: "lm-studio",
                    endpoint: new Uri("http://localhost:1234/v1")
                )
                .Build();
            ChatService = Kernel.GetRequiredService<IChatCompletionService>();
        }

        public static async Task<string> RunAsync(string content)
        {
            var chatHistory = new ChatHistory();

            const string prompt = """
Generate an AZ-900 exam question in this raw JSON format:

{
  "question": "<Insert the full exam-style question here>",
  "options": {
      "A": "<Option A>",
      "B": "<Option B>",
      "C": "<Option C>",
      "D": "<Option D>"
  },
  "correctAnswer": "<Letter of the correct answer>",
  "correctAnswerText": "<Full text of the correct answer>",
  "topic": "<Topic name>",
  "explanation": "<Brief explanation of why this is the correct answer>",
  "notes": [
      "<Optional note or tip 1>",
      "<Optional note or tip 2>"
  ]
}

Ensure the JSON is valid and ready to parse. Make the question exam-style and Azure accurate.
""";

            chatHistory.AddSystemMessage(prompt);
            chatHistory.AddUserMessage(content);

            var response = await ChatService.GetChatMessageContentAsync(
                chatHistory: chatHistory,
                kernel: Kernel
            );

            return response.ToString();
        }
    }

    /// <summary>
    /// Utilities to convert internal questions into Anki notes and import them via AnkiConnect.
    /// </summary>
    public static class Anki
    {
        private const int AnkiConnectVersion = 6;

        private record AnkiConnectNote(
            [property: JsonPropertyName("deckName")] string DeckName,
            [property: JsonPropertyName("modelName")] string ModelName,
            [property: JsonPropertyName("fields")] Dictionary<string, string> Fields,
            [property: JsonPropertyName("tags")] string[] Tags
        );

        private static AIResponse? ParseAIResponse(string aiJson)
        {
            if (string.IsNullOrWhiteSpace(aiJson))
                return null;

            try
            {
                var jsonText = aiJson.Trim();
                if (jsonText.Contains("```json"))
                {
                    var startIdx = jsonText.IndexOf("```json", StringComparison.OrdinalIgnoreCase) + 7;
                    var endIdx = jsonText.IndexOf("```", startIdx, StringComparison.OrdinalIgnoreCase);
                    if (endIdx > startIdx)
                    {
                        jsonText = jsonText.Substring(startIdx, endIdx - startIdx).Trim();
                    }
                }
                else if (jsonText.Contains("```"))
                {
                    var startIdx = jsonText.IndexOf("```", StringComparison.OrdinalIgnoreCase) + 3;
                    var endIdx = jsonText.IndexOf("```", startIdx, StringComparison.OrdinalIgnoreCase);
                    if (endIdx > startIdx)
                    {
                        jsonText = jsonText.Substring(startIdx, endIdx - startIdx).Trim();
                    }
                }

                return JsonSerializer.Deserialize<AIResponse>(jsonText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        private static string FormatAIResponseAsHTML(AIResponse? aiResponse, string fallbackJson)
        {
            if (aiResponse == null)
            {
                return $"<pre>{System.Net.WebUtility.HtmlEncode(fallbackJson)}</pre>";
            }

            var html = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(aiResponse.Question))
            {
                html.AppendLine(
                    $"<div style='font-weight: bold; font-size: 1.1em; margin-bottom: 10px;'>{System.Net.WebUtility.HtmlEncode(aiResponse.Question)}</div>");
            }

            if (aiResponse.Options != null && aiResponse.Options.Count > 0)
            {
                html.AppendLine("<div style='margin-bottom: 10px;'>");
                html.AppendLine("<strong>Options:</strong>");
                html.AppendLine("<ul style='margin-top: 5px;'>");

                var correctAnswers = aiResponse.CorrectAnswer?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => a.Trim().ToUpperInvariant())
                    .ToHashSet() ?? new HashSet<string>();

                foreach (var option in aiResponse.Options.OrderBy(o => o.Key))
                {
                    var isCorrect = correctAnswers.Contains(option.Key.ToUpperInvariant());
                    var style = isCorrect
                        ? "color: green; font-weight: bold;"
                        : "";
                    html.AppendLine(
                        $"<li style='{style}'><strong>{option.Key}:</strong> {System.Net.WebUtility.HtmlEncode(option.Value)}</li>");
                }
                html.AppendLine("</ul>");
                html.AppendLine("</div>");
            }

            if (!string.IsNullOrWhiteSpace(aiResponse.CorrectAnswer))
            {
                html.AppendLine("<div style='margin-bottom: 10px;'>");
                html.AppendLine(
                    $"<strong>Correct Answer:</strong> <span style='color: green; font-weight: bold;'>{System.Net.WebUtility.HtmlEncode(aiResponse.CorrectAnswer)}</span>");
                if (!string.IsNullOrWhiteSpace(aiResponse.CorrectAnswerText))
                {
                    html.AppendLine(
                        $"<div style='margin-left: 20px; margin-top: 5px;'>{System.Net.WebUtility.HtmlEncode(aiResponse.CorrectAnswerText)}</div>");
                }
                html.AppendLine("</div>");
            }

            if (!string.IsNullOrWhiteSpace(aiResponse.Explanation))
            {
                html.AppendLine("<div style='margin-bottom: 10px;'>");
                html.AppendLine("<strong>Explanation:</strong>");
                html.AppendLine(
                    $"<div style='margin-left: 20px; margin-top: 5px;'>{System.Net.WebUtility.HtmlEncode(aiResponse.Explanation)}</div>");
                html.AppendLine("</div>");
            }

            if (!string.IsNullOrWhiteSpace(aiResponse.Topic))
            {
                html.AppendLine($"<div style='margin-bottom: 10px;'><em>Topic: {System.Net.WebUtility.HtmlEncode(aiResponse.Topic)}</em></div>");
            }

            if (aiResponse.Notes != null && aiResponse.Notes.Count > 0)
            {
                html.AppendLine("<div style='margin-bottom: 10px;'>");
                html.AppendLine("<strong>Notes:</strong>");
                html.AppendLine("<ul style='margin-top: 5px;'>");
                foreach (var note in aiResponse.Notes)
                {
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        html.AppendLine($"<li>{System.Net.WebUtility.HtmlEncode(note)}</li>");
                    }
                }
                html.AppendLine("</ul>");
                html.AppendLine("</div>");
            }

            return html.ToString();
        }

        private static string CleanQuestionContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return content;

            var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var cleanedLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (trimmedLine.StartsWith("Correct Answer:", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("References:", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("Select and Place:", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.Contains("Highly Voted", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.Contains("upvoted", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.Contains("Most Recent", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    (trimmedLine.Length > 0 && trimmedLine[0] == '\uF147'))
                {
                    break;
                }

                if (cleanedLines.Count == 0 && string.IsNullOrWhiteSpace(trimmedLine))
                    continue;

                cleanedLines.Add(trimmedLine);
            }

            return string.Join("\n", cleanedLines).Trim();
        }

        private static string FormatOptionsForFront(AIResponse? aiResponse)
        {
            if (aiResponse?.Options == null || aiResponse.Options.Count == 0)
                return string.Empty;

            var html = new StringBuilder();
            html.AppendLine("<div style='margin-top: 15px;'>");
            html.AppendLine("<ul style='list-style-type: none; padding-left: 0; margin: 10px 0;'>");

            foreach (var option in aiResponse.Options.OrderBy(o => o.Key))
            {
                html.AppendLine(
                    $"<li style='margin: 8px 0; padding: 5px;'><strong>{option.Key}:</strong> {System.Net.WebUtility.HtmlEncode(option.Value)}</li>");
            }

            html.AppendLine("</ul>");
            html.AppendLine("</div>");

            return html.ToString();
        }

        internal static List<object> BuildNotes(
            IEnumerable<Question> questions,
            string deckName = "Default",
            string modelName = "Basic",
            string[]? tags = null)
        {
            tags ??= ["az900"];
            var notes = new List<object>();

            foreach (var q in questions)
            {
                if (q.AIParsed == null && string.IsNullOrWhiteSpace(q.AIRaw))
                    continue;

                var rawContent = (q.Raw ?? string.Empty).Trim();
                var cleanedContent = CleanQuestionContent(rawContent);
                var header = (q.Header ?? string.Empty).Trim();

                var aiResponse = q.AIParsed != null
                    ? new AIResponse
                    {
                        Question = q.AIQuestion,
                        Options = q.AIOptions,
                        CorrectAnswer = q.AICorrectAnswer,
                        CorrectAnswerText = q.AICorrectAnswerText,
                        Topic = q.AITopic,
                        Explanation = q.AIExplanation,
                        Notes = q.AINotes
                    }
                    : ParseAIResponse(q.AIRaw);

                var optionsHtml = FormatOptionsForFront(aiResponse);

                var frontBuilder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(header))
                {
                    frontBuilder.AppendLine(
                        $"<div style='font-weight: bold; color: #666; font-size: 0.9em; margin-bottom: 10px;'>{System.Net.WebUtility.HtmlEncode(header)}</div>");
                }
                frontBuilder.AppendLine($"<div>{System.Net.WebUtility.HtmlEncode(cleanedContent)}</div>");
                if (!string.IsNullOrWhiteSpace(optionsHtml))
                {
                    frontBuilder.Append(optionsHtml);
                }
                var front = frontBuilder.ToString();

                var backHtml = FormatAIResponseAsHTML(aiResponse, q.AIRaw);
                var back = string.IsNullOrWhiteSpace(header)
                    ? backHtml
                    : $"<div style='font-weight: bold; color: #999; font-size: 0.85em; margin-bottom: 8px; border-bottom: 1px solid #ddd; padding-bottom: 5px;'>{System.Net.WebUtility.HtmlEncode(header)}</div>\n{backHtml}";

                var fields = new Dictionary<string, string>
                {
                    ["Front"] = front,
                    ["Back"] = back
                };

                notes.Add(new AnkiConnectNote(deckName, modelName, fields, tags));
            }

            return notes;
        }

        private class AIResponse
        {
            [JsonPropertyName("question")]
            public string? Question { get; set; }

            [JsonPropertyName("options")]
            public Dictionary<string, string>? Options { get; set; }

            [JsonPropertyName("correctAnswer")]
            public string? CorrectAnswer { get; set; }

            [JsonPropertyName("correctAnswerText")]
            public string? CorrectAnswerText { get; set; }

            [JsonPropertyName("topic")]
            public string? Topic { get; set; }

            [JsonPropertyName("explanation")]
            public string? Explanation { get; set; }

            [JsonPropertyName("notes")]
            public List<string>? Notes { get; set; }
        }

        internal static void SaveAsJson(IEnumerable<Question> questions, string filePath, string deckName = "Default", string modelName = "Basic")
        {
            var notes = BuildNotes(questions, deckName, modelName);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(notes, options);
            File.WriteAllText(filePath, json);
        }

        internal static async Task<JsonElement> AddToAnkiAsync(
            IEnumerable<Question> questions,
            string deckName = "Default",
            string modelName = "Basic",
            string ankiConnectUrl = "http://127.0.0.1:8765")
        {
            var notes = BuildNotes(questions, deckName, modelName);

            using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(20) };

            var pingReq = new { action = "version", version = AnkiConnectVersion };
            try
            {
                var pingResp = await http.PostAsJsonAsync(ankiConnectUrl, pingReq).ConfigureAwait(false);
                if (!pingResp.IsSuccessStatusCode)
                {
                    SaveFailedRequest(questions, "ankiconnect-ping-failed.json");
                    throw new InvalidOperationException(
                        $"AnkiConnect not responding (status {pingResp.StatusCode}). Ensure Anki + AnkiConnect are running.");
                }
            }
            catch (Exception ex)
            {
                SaveFailedRequest(questions, "ankiconnect-ping-failed.json");
                throw new InvalidOperationException(
                    $"Unable to contact AnkiConnect at {ankiConnectUrl}. Ensure Anki with AnkiConnect is running and reachable. Notes saved to 'ankiconnect-ping-failed.json'. Inner: {ex.Message}",
                    ex);
            }

            var request = new
            {
                action = "addNotes",
                version = AnkiConnectVersion,
                @params = new { notes }
            };

            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var resp = await http.PostAsync(ankiConnectUrl, content).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                {
                    SaveFailedRequest(questions, "ankiconnect-error.json");
                    throw new InvalidOperationException($"AnkiConnect returned error: {err}");
                }

                return doc.RootElement.GetProperty("result");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
            {
                SaveFailedRequest(questions, "ankiconnect-failed.json");
                throw new InvalidOperationException(
                    $"Failed to send notes to AnkiConnect: {ex.Message}. Notes saved to 'ankiconnect-failed.json' for manual import.",
                    ex);
            }
        }

        internal static async Task ExportAndOptionallyPushAsync(
            IEnumerable<Question> questions,
            string filePath = "anki-import.json",
            bool push = false,
            string deckName = "Default",
            string modelName = "Basic",
            string ankiConnectUrl = "http://127.0.0.1:8765")
        {
            SaveAsJson(questions, filePath, deckName, modelName);

            if (push)
            {
                await AddToAnkiAsync(questions, deckName, modelName, ankiConnectUrl).ConfigureAwait(false);
            }
        }

        private static void SaveFailedRequest(IEnumerable<Question> questions, string fileName)
        {
            try
            {
                SaveAsJson(questions, fileName);
            }
            catch
            {
                // best-effort save; swallow any errors to avoid masking the original exception
            }
        }
    }
}

namespace Default.Data
{
    /// <summary>
    /// Lightweight repository for persisting questions and AI results to SQLite.
    /// </summary>
    public class QuestionRepository
    {
        private readonly string _connectionString;

        public QuestionRepository(string dbPath)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
        }

        public async Task EnsureCreatedAsync()
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS questions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    header TEXT NOT NULL UNIQUE,
                    raw TEXT,
                    airaw TEXT,
                    aiquestion TEXT,
                    aioptions TEXT,
                    aicorrectanswer TEXT,
                    aicorrectanswertext TEXT,
                    aitopic TEXT,
                    aiexplanation TEXT,
                    ainotes TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_questions_has_ai ON questions(airaw, aiquestion);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<bool> HasAIAsync(string header)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM questions WHERE header = $header AND (airaw <> '' OR aiquestion <> '')";
            cmd.Parameters.AddWithValue("$header", header ?? string.Empty);
            var result = await cmd.ExecuteScalarAsync();
            return result != null && Convert.ToInt64(result) > 0;
        }

        public async Task UpsertAsync(Default.Question q)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO questions
                (header, raw, airaw, aiquestion, aioptions, aicorrectanswer, aicorrectanswertext, aitopic, aiexplanation, ainotes, updated_at)
                VALUES ($header, $raw, $airaw, $aiquestion, $aioptions, $aicorrectanswer, $aicorrectanswertext, $aitopic, $aiexplanation, $ainotes, datetime('now'))
                ON CONFLICT(header) DO UPDATE SET
                    raw = excluded.raw,
                    airaw = excluded.airaw,
                    aiquestion = excluded.aiquestion,
                    aioptions = excluded.aioptions,
                    aicorrectanswer = excluded.aicorrectanswer,
                    aicorrectanswertext = excluded.aicorrectanswertext,
                    aitopic = excluded.aitopic,
                    aiexplanation = excluded.aiexplanation,
                    ainotes = excluded.ainotes,
                    updated_at = datetime('now');
                """;
            cmd.Parameters.AddWithValue("$header", q.Header ?? string.Empty);
            cmd.Parameters.AddWithValue("$raw", q.Raw ?? string.Empty);
            cmd.Parameters.AddWithValue("$airaw", q.AIRaw ?? string.Empty);
            cmd.Parameters.AddWithValue("$aiquestion", q.AIQuestion ?? string.Empty);
            cmd.Parameters.AddWithValue("$aioptions", q.AIOptions != null ? JsonSerializer.Serialize(q.AIOptions) : string.Empty);
            cmd.Parameters.AddWithValue("$aicorrectanswer", q.AICorrectAnswer ?? string.Empty);
            cmd.Parameters.AddWithValue("$aicorrectanswertext", q.AICorrectAnswerText ?? string.Empty);
            cmd.Parameters.AddWithValue("$aitopic", q.AITopic ?? string.Empty);
            cmd.Parameters.AddWithValue("$aiexplanation", q.AIExplanation ?? string.Empty);
            cmd.Parameters.AddWithValue("$ainotes", q.AINotes != null ? JsonSerializer.Serialize(q.AINotes) : string.Empty);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<Default.Question>> GetAllAsync(bool onlyWithAI = true)
        {
            var list = new List<Default.Question>();
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = onlyWithAI
                ? "SELECT header, raw, airaw, aiquestion, aioptions, aicorrectanswer, aicorrectanswertext, aitopic, aiexplanation, ainotes FROM questions WHERE (airaw <> '' OR aiquestion <> '')"
                : "SELECT header, raw, airaw, aiquestion, aioptions, aicorrectanswer, aicorrectanswertext, aitopic, aiexplanation, ainotes FROM questions";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var q = new Default.Question(reader.GetString(0))
                {
                    Raw = reader.GetString(1),
                    AIRaw = reader.GetString(2),
                    AIQuestion = reader.IsDBNull(3) ? null : reader.GetString(3),
                    AIOptions = DeserializeDict(reader.IsDBNull(4) ? null : reader.GetString(4)),
                    AICorrectAnswer = reader.IsDBNull(5) ? null : reader.GetString(5),
                    AICorrectAnswerText = reader.IsDBNull(6) ? null : reader.GetString(6),
                    AITopic = reader.IsDBNull(7) ? null : reader.GetString(7),
                    AIExplanation = reader.IsDBNull(8) ? null : reader.GetString(8),
                    AINotes = DeserializeList(reader.IsDBNull(9) ? null : reader.GetString(9))
                };

                if (!string.IsNullOrWhiteSpace(q.AIQuestion) || (q.AIOptions != null && q.AIOptions.Count > 0))
                {
                    q.AIParsed = new Default.AIParsed
                    {
                        Question = q.AIQuestion,
                        Options = q.AIOptions,
                        CorrectAnswer = q.AICorrectAnswer,
                        CorrectAnswerText = q.AICorrectAnswerText,
                        Topic = q.AITopic,
                        Explanation = q.AIExplanation,
                        Notes = q.AINotes
                    };
                }
                list.Add(q);
            }

            return list;
        }

        private static Dictionary<string, string>? DeserializeDict(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); } catch { return null; }
        }

        private static List<string>? DeserializeList(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<List<string>>(json); } catch { return null; }
        }
    }
}