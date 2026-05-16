using System;
using System.Collections.Generic;

namespace Feil.Models;

public class PersistedHistoryState
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public List<HistoryEntry> Entries { get; set; } = [];
}
