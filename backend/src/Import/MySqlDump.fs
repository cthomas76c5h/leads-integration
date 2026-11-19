/// Reads rows out of a mysqldump file without a MySQL server: the column names
/// from `CREATE TABLE`, the values from the extended `INSERT INTO ... VALUES
/// (...),(...);` lines. Enough for the chatbot backup; not a general SQL parser.
module LeadIntegrations.Importers.MySqlDump

open System
open System.Collections.Generic
open System.IO
open System.Text

/// One row: column name → value (null for SQL NULL).
type Row = IReadOnlyDictionary<string, string>

let private columnsOf (lines: string[]) (table: string) : string[] =
    let start = lines |> Array.tryFindIndex (fun l -> l.StartsWith $"CREATE TABLE `{table}`")
    match start with
    | None -> [||]
    | Some i ->
        lines
        |> Array.skip (i + 1)
        |> Array.takeWhile (fun l -> not (l.StartsWith ")"))
        |> Array.choose (fun l ->
            let t = l.Trim()
            if t.StartsWith "`" then Some(t.Substring(1, t.IndexOf('`', 1) - 1)) else None)

/// Splits `(1,'a\'b',NULL),(2,...)` into rows of values.
let parseValues (text: string) : string list list =
    let rows = ResizeArray<string list>()
    let mutable i = 0
    let n = text.Length
    let current = ResizeArray<string>()
    let sb = StringBuilder()
    let mutable inRow = false
    while i < n do
        let c = text[i]
        if not inRow then
            if c = '(' then
                inRow <- true
                current.Clear()
            i <- i + 1
        elif c = '\'' then
            // A quoted string with backslash escapes.
            sb.Clear() |> ignore
            i <- i + 1
            let mutable closed = false
            while not closed && i < n do
                let ch = text[i]
                if ch = '\\' && i + 1 < n then
                    let e = text[i + 1]
                    sb.Append(
                        match e with
                        | 'n' -> '\n'
                        | 'r' -> '\r'
                        | 't' -> '\t'
                        | '0' -> '\000'
                        | 'Z' -> '\026'
                        | other -> other
                    )
                    |> ignore
                    i <- i + 2
                elif ch = '\'' && i + 1 < n && text[i + 1] = '\'' then
                    sb.Append '\'' |> ignore
                    i <- i + 2
                elif ch = '\'' then
                    closed <- true
                    i <- i + 1
                else
                    sb.Append ch |> ignore
                    i <- i + 1
            current.Add(sb.ToString())
            // Skip to the separator.
            while i < n && text[i] <> ',' && text[i] <> ')' do
                i <- i + 1
            if i < n && text[i] = ')' then
                rows.Add(List.ofSeq current)
                inRow <- false
            i <- i + 1
        elif c = ',' || c = ' ' then
            i <- i + 1
        else
            // An unquoted value: number, NULL, or _binary/0x literal.
            let start = i
            while i < n && text[i] <> ',' && text[i] <> ')' do
                i <- i + 1
            let raw = text.Substring(start, i - start).Trim()
            current.Add(if raw.Equals("NULL", StringComparison.OrdinalIgnoreCase) then null else raw)
            if i < n && text[i] = ')' then
                rows.Add(List.ofSeq current)
                inRow <- false
            i <- i + 1
    List.ofSeq rows

/// Every row of `table` in the dump.
let rows (dumpPath: string) (table: string) : Row list =
    let lines = File.ReadAllLines(dumpPath, Encoding.UTF8)
    let cols = columnsOf lines table
    if cols.Length = 0 then []
    else
        let prefix = $"INSERT INTO `{table}` VALUES "
        lines
        |> Seq.filter (fun l -> l.StartsWith prefix)
        |> Seq.collect (fun l -> parseValues (l.Substring(prefix.Length).TrimEnd(';')))
        |> Seq.map (fun values ->
            let d = Dictionary<string, string>()
            values |> List.iteri (fun i v -> if i < cols.Length then d[cols[i]] <- v)
            d :> Row)
        |> List.ofSeq

let get (r: Row) (col: string) =
    match r.TryGetValue col with
    | true, v -> v
    | _ -> null
