set script-interpreter := ['bash', '-eu']

test:
    dotnet test

read INPATH:
    dotnet run --project "MZPeakNet.AppTest" -- --verbose read {{INPATH}}

[positional-arguments]
convert-thermo INPATH OUTPATH *args='':
    dotnet run --project "MZPeakNet.AppTest" -- --verbose thermo {{args}} {{INPATH}} {{OUTPATH}}