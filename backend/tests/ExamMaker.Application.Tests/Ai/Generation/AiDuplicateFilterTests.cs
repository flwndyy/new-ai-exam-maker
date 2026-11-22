using System.Text;
using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Tests.Ai.Generation;

public sealed class AiDuplicateFilterTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AiGeneratedQuestion Mc(int ordinal, string stem, string a, string b, string c, string d, string correct) =>
        new(ordinal, stem, Json($$"""
            {"options":[{"label":"A","text":{{JsonSerializer.Serialize(a)}}},{"label":"B","text":{{JsonSerializer.Serialize(b)}}},{"label":"C","text":{{JsonSerializer.Serialize(c)}}},{"label":"D","text":{{JsonSerializer.Serialize(d)}}}],"correctLabel":"{{correct}}"}
            """), null);

    private static AiGeneratedQuestion Tf(int ordinal, string stem, string a, string b, string c, string d) =>
        new(ordinal, stem, Json($$"""
            {"statements":[{"label":"a","text":{{JsonSerializer.Serialize(a)}},"isTrue":true},{"label":"b","text":{{JsonSerializer.Serialize(b)}},"isTrue":false},{"label":"c","text":{{JsonSerializer.Serialize(c)}},"isTrue":true},{"label":"d","text":{{JsonSerializer.Serialize(d)}},"isTrue":false}]}
            """), null);

    private static JsonElement ShortPayload() =>
        Json("""{"answerType":"number","acceptedAnswers":["12"],"unit":"m","tolerance":0.01}""");

    private static JsonElement EssayPayload() =>
        Json("""{"modelAnswer":"x","rubric":[{"criterion":"c","points":1}]}""");

    private static string Key(AiGeneratedQuestion q) =>
        AiDuplicateFilter.Key(q.Payload.TryGetProperty("options", out _) ? "MultipleChoice" : "TrueFalse", q.Stem, q.Payload);

    [Fact]
    public void Normalize_collapses_whitespace_lowercases_and_composes()
        => Assert.Equal("cho hàm số y = x^2", AiTextNormalizer.Normalize("  Cho\tHÀM   số\n y = x^2 "));

    [Fact]
    public void Nfd_and_nfc_vietnamese_produce_same_key()
    {
        var nfc = "Phương trình bậc hai".Normalize(NormalizationForm.FormC);
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfc, nfd);
        Assert.Equal(
            AiDuplicateFilter.Key("ShortAnswer", nfc, ShortPayload()),
            AiDuplicateFilter.Key("ShortAnswer", nfd, ShortPayload()));
    }

    [Fact]
    public void Mc_key_ignores_correct_label_but_includes_option_texts()
    {
        Assert.Equal(Key(Mc(1, "Câu", "1", "2", "3", "4", "A")), Key(Mc(2, "Câu", "1", "2", "3", "4", "C")));
        Assert.NotEqual(Key(Mc(1, "Câu", "1", "2", "3", "4", "A")), Key(Mc(2, "Câu", "1", "2", "3", "5", "A")));
    }

    [Fact]
    public void Tf_with_generic_stem_and_different_statements_is_not_duplicate()
    {
        var result = AiDuplicateFilter.Filter(
            "TrueFalse",
            [Tf(1, "Cho các phát biểu sau", "p", "q", "r", "s"), Tf(2, "Cho các phát biểu sau", "p", "q", "r", "t")],
            new HashSet<string>());

        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(0, result.Dropped);
    }

    [Fact]
    public void In_batch_duplicate_keeps_lowest_ordinal()
    {
        var result = AiDuplicateFilter.Filter(
            "MultipleChoice",
            [Mc(1, "Câu A", "1", "2", "3", "4", "A"), Mc(2, " câu a ", "1", "2", "3", "4", "B"), Mc(3, "Câu B", "1", "2", "3", "4", "A")],
            new HashSet<string>());

        Assert.Equal([1, 3], result.Kept.Select(q => q.Ordinal));
        Assert.Equal(1, result.Dropped);
    }

    [Fact]
    public void Bank_duplicate_is_dropped_and_ordinals_keep_gaps()
    {
        var bank = new HashSet<string> { Key(Mc(9, "Câu B", "1", "2", "3", "4", "D")) };
        var result = AiDuplicateFilter.Filter(
            "MultipleChoice",
            [Mc(1, "Câu A", "1", "2", "3", "4", "A"), Mc(2, "Câu B", "1", "2", "3", "4", "A"), Mc(3, "Câu C", "1", "2", "3", "4", "A")],
            bank);

        Assert.Equal([1, 3], result.Kept.Select(q => q.Ordinal));
        Assert.Equal(1, result.Dropped);
    }

    [Fact]
    public void All_duplicates_returns_empty_kept()
    {
        var q = Mc(1, "Câu A", "1", "2", "3", "4", "A");
        var result = AiDuplicateFilter.Filter("MultipleChoice", [q], new HashSet<string> { Key(q) });

        Assert.Empty(result.Kept);
        Assert.Equal(1, result.Dropped);
    }

    [Fact]
    public void Filter_does_not_mutate_existing_keys()
    {
        var existing = new HashSet<string>();
        AiDuplicateFilter.Filter("MultipleChoice", [Mc(1, "Câu A", "1", "2", "3", "4", "A")], existing);

        Assert.Empty(existing);
    }

    [Fact]
    public void ShortAnswer_keys_use_stem_only_within_type()
        => Assert.Equal(
            AiDuplicateFilter.Key("ShortAnswer", "Tính x", ShortPayload()),
            AiDuplicateFilter.Key("ShortAnswer", "tính  X", Json("""{"answerType":"text","acceptedAnswers":["khác"]}""")));

    [Fact]
    public void Different_types_with_same_stem_do_not_collide()
        => Assert.NotEqual(
            AiDuplicateFilter.Key("ShortAnswer", "Tính x", ShortPayload()),
            AiDuplicateFilter.Key("Essay", "Tính x", EssayPayload()));
}
