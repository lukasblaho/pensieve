-- PensieveNotifier.applescript
--
-- Compiled (via `osacompile`) into Pensieve.app at install time so macOS Notification
-- Center attributes Pensieve's alerts to "Pensieve" instead of the generic "Script Editor"
-- identity osascript normally uses for one-off `display notification` calls.
--
-- Invoked as: Pensieve.app/Contents/MacOS/Pensieve <title> <subtitle> <message> <sound>
-- <subtitle> and <sound> may be empty strings, in which case the corresponding
-- `display notification` clause is omitted.

on run argv
    set theTitle to item 1 of argv
    set theSubtitle to item 2 of argv
    set theMessage to item 3 of argv
    set theSound to item 4 of argv

    if theSubtitle is "" and theSound is "" then
        display notification theMessage with title theTitle
    else if theSubtitle is "" then
        display notification theMessage with title theTitle sound name theSound
    else if theSound is "" then
        display notification theMessage with title theTitle subtitle theSubtitle
    else
        display notification theMessage with title theTitle subtitle theSubtitle sound name theSound
    end if
end run
