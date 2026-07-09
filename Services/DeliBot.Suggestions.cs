using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DeliAi.Backend.Services;

public partial class DeliBot
{
  private async Task PruneUnansweredQuestionsAsync()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();

    var unanswered = await db.UnansweredQuestions.OrderBy(u => u.Id).ToListAsync();
    if (unanswered.Count == 0) return;

    var facts = await db.Facts.Where(f => f.Info != DefaultRefusalRule).ToListAsync();

    string questionList = string.Join("\n", unanswered.Select(e => $"- {e.Info}"));
    string factList = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    string systemPrompt =
        "You are checking a log of customer questions that a deli assistant bot could NOT answer at " +
        "the time they were asked, because no matching fact existed in its knowledge base at that time.\n\n" +
        $"CURRENT KNOWN FACTS:\n{factList}\n\n" +
        $"PREVIOUSLY UNANSWERED QUESTIONS:\n{questionList}\n\n" +
        "Check each unanswered question against the CURRENT KNOWN FACTS list. If a fact now covers that " +
        "question's topic (even if worded differently), that question is now answered and should be " +
        "REMOVED from consideration. ALSO if the unanswered question includes any curses, swears, or slurs, it should be REMOVED from consideration. Otherwise keep it.\n\n" +
        "Respond with ONLY a JSON object shaped exactly like this, nothing else — no markdown, no " +
        "backticks, no preamble:\n" +
        "{\n" +
        "  \"stillUnanswered\": [\"<questions from the input list that are still NOT answered, copied verbatim>\"]\n" +
        "}";

    var requestBody = new
    {
      contents = new[]
      {
        new { role = "user", parts = new[] { new { text = "Check the unanswered questions against current facts." } } }
      },
      systemInstruction = new
      {
        parts = new[] { new { text = systemPrompt } }
      }
    };

    string json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={_apiKey}";

    try
    {
      var response = await _http.PostAsync(url, content);
      string responseText = await response.Content.ReadAsStringAsync();

      if (!response.IsSuccessStatusCode)
      {
        Console.WriteLine($"[Prune error] Gemini API returned failure: {responseText}");
        return; // fail safe: leave the unanswered log untouched
      }

      using var doc = JsonDocument.Parse(responseText);
      var answer = doc.RootElement
          .GetProperty("candidates")[0]
          .GetProperty("content")
          .GetProperty("parts")[0]
          .GetProperty("text")
          .GetString();

      string cleaned = (answer ?? "{}").Trim();
      if (cleaned.StartsWith("```"))
      {
        int firstNewline = cleaned.IndexOf('\n');
        int lastFence = cleaned.LastIndexOf("```");
        if (firstNewline >= 0 && lastFence > firstNewline)
          cleaned = cleaned.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
      }

      using var resultDoc = JsonDocument.Parse(cleaned);
      var root = resultDoc.RootElement;

      var stillUnanswered = root.TryGetProperty("stillUnanswered", out var su)
          ? su.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : null;

      if (stillUnanswered == null) return; // couldn't parse, don't wipe the log

      var toRemove = unanswered.Where(e => !stillUnanswered.Contains(e.Info)).ToList();
      if (toRemove.Count > 0)
      {
        db.UnansweredQuestions.RemoveRange(toRemove);
        await db.SaveChangesAsync();
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Prune error] {ex.Message}");
      // fail safe: leave the unanswered log as-is rather than risk wiping it on a bad response
    }
  }

  public async Task<List<string>> getSuggestions()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();

    var unanswered = await db.UnansweredQuestions.OrderBy(e => e.Id).ToListAsync();

    if (unanswered.Count == 0)
    {
      return new List<string> { "No unanswered questions yet — nothing to suggest." };
    }

    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).ToListAsync();

    string questionList = string.Join("\n", unanswered.Select(e => $"- {e.Info}"));
    string factList = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    string systemPrompt =
        "You are analyzing a log of customer questions that a deli assistant bot could NOT answer at " +
        "the time they were asked, because no matching fact existed in its knowledge base at that time.\n\n" +
        $"CURRENT KNOWN FACTS:\n{factList}\n\n" +
        $"PREVIOUSLY UNANSWERED QUESTIONS:\n{questionList}\n\n" +
        "STEP 1: Some of these questions may have since been answered — check each unanswered question " +
        "against the CURRENT KNOWN FACTS list. If a fact now covers that question's topic (even if worded " +
        "differently), that question is now answered and should be REMOVED from consideration entirely.\n\n" +
        "STEP 2: Group the REMAINING (still-unanswered) questions by underlying topic/intent, not by exact " +
        "wording. Two questions belong in the same group if they are asking about the same general thing, " +
        "even if phrased completely differently (e.g. \"where is the coleslaw\" and \"bags of slaw are " +
        "where\" are the SAME topic). Find the 3 topics with the most questions grouped under them, AS LONG AS THERE ARE AT LEAST 2 ENTRIES FOR THAT TOPIC. If " +
        "there is a tie for 3rd place, pick whichever tied topic seems most useful for a deli to add.\n\n" +
        "For each of the 3 topics, write ONE specific, concrete suggestion that names the actual thing to " +
        "add — reuse the specific noun/subject from the original question itself, don't generalize it into " +
        "a broader category. For example, if the question was \"who is the store manager\", the suggestion " +
        "must be \"Add the name of the store manager.\" — NOT a vague broader thing like \"Add contact info " +
        "for store management.\" Stay as close to the original question's specific subject as possible.\n\n" +
        "Keep suggestions simple and concise, plain everyday wording, no fancy phrasing, but don't drop " +
        "important specific details from the original question.\n\n" +
        "If there are fewer than 3 distinct still-unanswered topics total, return fewer than 3 suggestions " +
        "rather than inventing filler ones.\n\n" +
        "Respond with ONLY a JSON object shaped exactly like this, nothing else — no markdown, no backticks, " +
        "no preamble:\n" +
        "{\n" +
        "  \"stillUnanswered\": [\"<questions from the input list that were NOT removed in step 1, copied verbatim>\"],\n" +
        "  \"suggestions\": [\"<up to 3 specific suggestion strings from step 2>\"]\n" +
        "}";

    var requestBody = new
    {
      contents = new[]
      {
      new { role = "user", parts = new[] { new { text = "Analyze the unanswered questions and return suggestions." } } }
    },
      systemInstruction = new
      {
        parts = new[] { new { text = systemPrompt } }
      }
    };

    string json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={_apiKey}";

    var response = await _http.PostAsync(url, content);
    string responseText = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
      Console.WriteLine($"[Gemini API error] {responseText}");
      return new List<string> { "Sorry, something went wrong generating suggestions." };
    }

    using var doc = JsonDocument.Parse(responseText);
    var answer = doc.RootElement
        .GetProperty("candidates")[0]
        .GetProperty("content")
        .GetProperty("parts")[0]
        .GetProperty("text")
        .GetString();

    string cleaned = (answer ?? "{}").Trim();

    if (cleaned.StartsWith("```"))
    {
      int firstNewline = cleaned.IndexOf('\n');
      int lastFence = cleaned.LastIndexOf("```");
      if (firstNewline >= 0 && lastFence > firstNewline)
        cleaned = cleaned.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
    }

    try
    {
      using var resultDoc = JsonDocument.Parse(cleaned);
      var root = resultDoc.RootElement;

      var stillUnanswered = root.TryGetProperty("stillUnanswered", out var su)
          ? su.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : new List<string>();

      var suggestions = root.TryGetProperty("suggestions", out var sg)
          ? sg.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").Take(3).ToList()
          : new List<string>();

      // Prune the log: only keep questions the model says are still unanswered
      var toRemove = unanswered.Where(e => !stillUnanswered.Contains(e.Info)).ToList();
      if (toRemove.Count > 0)
      {
        db.UnansweredQuestions.RemoveRange(toRemove);
        await db.SaveChangesAsync();
      }

      return suggestions.Count > 0
          ? suggestions
          : new List<string> { "No suggestions right now — recent questions may already be answered." };
    }
    catch (JsonException ex)
    {
      Console.WriteLine($"[Suggestions parse error] Could not parse model response as JSON: {ex.Message}");
      Console.WriteLine($"[Suggestions raw response] {cleaned}");
      return new List<string> { "Couldn't generate suggestions right now — please try again." };
    }
  }

  public async Task<List<string>> removeSuggestion(string suggestion)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();

    var unanswered = await db.UnansweredQuestions.OrderBy(e => e.Id).ToListAsync();
    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).ToListAsync();

    string questionList = string.Join("\n", unanswered.Select(e => $"- {e.Info}"));
    string factList = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    string systemPrompt =
      "You are helping maintain a deli assistant bot's knowledge base.\n\n" +
      "The user has chosen to REMOVE a suggestion that the bot previously generated.\n" +
      "A suggestion corresponds to one or more unanswered customer questions.\n\n" +
      "Your job is to determine EXACTLY which unanswered questions should be removed — and to be " +
      "conservative. It is much worse to remove a question that doesn't belong than to leave one behind.\n\n" +
      "You will be given:\n" +
      "- The suggestion the user wants removed\n" +
      "- The full list of CURRENT KNOWN FACTS\n" +
      "- The full list of PREVIOUSLY UNANSWERED QUESTIONS\n\n" +
      "RULES:\n" +
      "1. A question relates to the suggestion ONLY if it is asking about the exact same specific " +
      "item, product, or topic named in the suggestion — not merely a similar, related, or same-category " +
      "item.\n\n" +
      "2. Treat different specific items as DIFFERENT topics even if they share a word or category. " +
      "For example: \"beans\" and \"refried beans\" are DIFFERENT items — do not match one for the other. " +
      "\"turkey\" and \"turkey club sandwich\" are DIFFERENT. \"cheese\" and \"cheddar cheese\" are " +
      "DIFFERENT unless the question is genuinely generic and non-specific. When in doubt about whether " +
      "two things are the same specific item, treat them as DIFFERENT and do not remove.\n\n" +
      "3. A question only counts as matching if a reasonable person would say the suggestion was " +
      "generated directly and specifically because of that question — not because they're topically " +
      "adjacent or in the same food category.\n\n" +
      "4. Remove ALL (and only) questions that pass rules 1–3. Do not remove anything else.\n\n" +
      "5. Respond ONLY with JSON in this exact shape, nothing else — no markdown, no backticks, no " +
      "preamble:\n" +
      "{\n" +
      "  \"remove\": [\"<list of unanswered questions to delete, copied verbatim>\"]\n" +
      "}\n";

    var requestBody = new
    {
      contents = new[]
        {
            new {
                role = "user",
                parts = new[] {
                    new {
                        text =
                            $"SUGGESTION TO REMOVE:\n{suggestion}\n\n" +
                            $"CURRENT KNOWN FACTS:\n{factList}\n\n" +
                            $"UNANSWERED QUESTIONS:\n{questionList}\n\n" +
                            "Return JSON only."
                    }
                }
            }
        },
      systemInstruction = new
      {
        parts = new[] { new { text = systemPrompt } }
      }
    };

    string json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={_apiKey}";

    var response = await _http.PostAsync(url, content);
    string responseText = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
      Console.WriteLine($"[Gemini API error] {responseText}");
      return await getSuggestions();
    }

    using var doc = JsonDocument.Parse(responseText);
    var answer = doc.RootElement
        .GetProperty("candidates")[0]
        .GetProperty("content")
        .GetProperty("parts")[0]
        .GetProperty("text")
        .GetString();

    string cleaned = (answer ?? "{}").Trim();

    if (cleaned.StartsWith("```"))
    {
      int firstNewline = cleaned.IndexOf('\n');
      int lastFence = cleaned.LastIndexOf("```");
      if (firstNewline >= 0 && lastFence > firstNewline)
        cleaned = cleaned.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
    }

    try
    {
      using var resultDoc = JsonDocument.Parse(cleaned);
      var root = resultDoc.RootElement;

      var toRemoveText = root.TryGetProperty("remove", out var rm)
          ? rm.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : new List<string>();

      if (toRemoveText.Count > 0)
      {
        var toRemove = unanswered.Where(q => toRemoveText.Contains(q.Info)).ToList();
        if (toRemove.Count > 0)
        {
          db.UnansweredQuestions.RemoveRange(toRemove);
          await db.SaveChangesAsync();
        }
      }

      // Return updated suggestions
      return await getSuggestions();
    }
    catch (JsonException ex)
    {
      Console.WriteLine($"[RemoveSuggestion parse error] {ex.Message}");
      Console.WriteLine($"[RemoveSuggestion raw response] {cleaned}");
      return await getSuggestions();
    }
  }

  public async Task<string> AddAnsweredSuggestionAsync(string suggestion, string rawAnswer)
  {
    string systemPrompt =
        "You are helping turn a short customer-service answer into a complete, standalone fact for a " +
        "deli assistant bot's knowledge base.\n\n" +
        "You will be given:\n" +
        "- SUGGESTION: a prompt describing what info was missing (e.g. \"Add whether the deli carries " +
        "gluten-free bread.\")\n" +
        "- RAW ANSWER: what the employee typed in response, which may be short, informal, or missing " +
        "context (e.g. \"yes we do\", \"9pm\", \"back cooler\").\n\n" +
        "Your job: combine the SUGGESTION and RAW ANSWER into ONE complete, standalone sentence (or short " +
        "set of sentences) that fully states the fact, with no missing context. Someone reading ONLY this " +
        "output — without ever seeing the suggestion — must understand exactly what it means.\n\n" +
        "RULES:\n" +
        "1. Pull the specific subject/item out of the SUGGESTION and restate it explicitly in the fact. " +
        "Never leave a fact that only makes sense next to the suggestion (e.g. never just output \"Yes we do\" " +
        "or \"9pm\" on their own).\n" +
        "2. Do not add any information that isn't implied by the SUGGESTION or stated in the RAW ANSWER. " +
        "Do not guess prices, hours, or specifics that weren't given.\n" +
        "3. If the RAW ANSWER already is a complete, standalone statement that includes the subject, keep " +
        "it close to as-is (light grammar cleanup only, no content changes).\n" +
        "4. If the RAW ANSWER is ambiguous, confusing, or doesn't seem to actually answer the SUGGESTION, " +
        "return your best faithful merge anyway — do not refuse, do not add caveats or commentary.\n" +
        "5. Keep it concise, plain, everyday wording — no fancy phrasing, no fluff.\n" +
        "6. Respond with ONLY the final fact text, nothing else — no quotes, no labels, no markdown, no " +
        "preamble, no explanation.";

    var requestBody = new
    {
      contents = new[]
      {
      new {
        role = "user",
        parts = new[] {
          new {
            text = $"SUGGESTION:\n{suggestion}\n\nRAW ANSWER:\n{rawAnswer}\n\nReturn the merged fact only."
          }
        }
      }
    },
      systemInstruction = new
      {
        parts = new[] { new { text = systemPrompt } }
      }
    };

    string json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={_apiKey}";

    string finalFact = rawAnswer; // fallback if anything goes wrong

    try
    {
      var response = await _http.PostAsync(url, content);
      string responseText = await response.Content.ReadAsStringAsync();

      if (response.IsSuccessStatusCode)
      {
        using var doc = JsonDocument.Parse(responseText);
        var answer = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (!string.IsNullOrWhiteSpace(answer))
          finalFact = answer.Trim();
      }
      else
      {
        Console.WriteLine($"[Merge answer error] Gemini API returned failure: {responseText}");
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Merge answer error] {ex.Message}");
    }

    // Save the merged (or fallback raw) fact using the existing add path
    return await AddFactAsync(finalFact);
  }
}
