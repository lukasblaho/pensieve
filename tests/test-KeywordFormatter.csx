#nullable enable
// test-KeywordFormatter.csx
// Verifies camelCase conversion used only for note.md keyword presentation.

#load "TestKit.csx"
#load "../src/KeywordFormatter.csx"

using System;

TestKit.Section("KeywordFormatter: converts multi-word phrases to camelCase");
{
    TestKit.Assert(KeywordFormatter.ToCamelCase("cold brew") == "coldBrew", "'cold brew' should become 'coldBrew'");
    TestKit.Assert(KeywordFormatter.ToCamelCase("release date") == "releaseDate", "'release date' should become 'releaseDate'");
    TestKit.Assert(KeywordFormatter.ToCamelCase("weekly sync meeting") == "weeklySyncMeeting", "three-word phrases should camelCase all subsequent words");
}

TestKit.Section("KeywordFormatter: single words are lowercased");
{
    TestKit.Assert(KeywordFormatter.ToCamelCase("Bloomridge") == "bloomridge", "single capitalized word should be fully lowercased");
    TestKit.Assert(KeywordFormatter.ToCamelCase("SPRINT") == "sprint", "all-caps single word should be fully lowercased");
}

TestKit.Section("KeywordFormatter: preserves non-ASCII characters/diacritics within words");
{
    TestKit.Assert(KeywordFormatter.ToCamelCase("AI nástroje") == "aiNástroje", "diacritics should be preserved when camelCasing");
}

TestKit.Section("KeywordFormatter: handles hyphen/underscore-separated terms and empty/whitespace input gracefully");
{
    TestKit.Assert(KeywordFormatter.ToCamelCase("cold-brew_coffee") == "coldBrewCoffee", "hyphens/underscores should be treated as word separators");
    TestKit.Assert(KeywordFormatter.ToCamelCase("") == "", "empty string should be returned unchanged");
    TestKit.Assert(KeywordFormatter.ToCamelCase("   ") == "   ", "whitespace-only string should be returned unchanged");
}
