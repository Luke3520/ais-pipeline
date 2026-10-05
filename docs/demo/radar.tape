# Records docs/demo/radar.gif. Re-record from the repository root, with the seven-day store at
# data/ais.db, by running:  vhs docs/demo/radar.tape
#
# https://github.com/charmbracelet/vhs

Output docs/demo/radar.gif

Set Shell "bash"
Set FontSize 14
Set Width 1200
Set Height 720
Set Padding 12
Set Theme "Builtin Dark"
Set TypingSpeed 45ms
Set Framerate 12

Hide
Type "dotnet build src/AisPipeline.Cli -c Release > /dev/null && alias ais='dotnet src/AisPipeline.Cli/bin/Release/net10.0/AisPipeline.Cli.dll' && clear"
Enter
Sleep 8s
Show

Type "ais radar --region kattegat --from 2026-09-04T19:30:00Z --speed 900"
Sleep 400ms
Enter
Sleep 2s
Hide
Sleep 4s
Show
Sleep 13s
Screenshot docs/demo/radar.png
Sleep 2s
Type "q"
Sleep 500ms

Type "ais log --mmsi 636025106"
Sleep 400ms
Enter
Sleep 5s
