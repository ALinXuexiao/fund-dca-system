@echo off
rem Renders a wrapper HTML to PNG with headless Chrome, launched OUTSIDE the DSH file sandbox
rem via explorer.exe. Parameters come from small files so no CLI args are needed.
rem   _j_html.txt = file:// URI of the wrapper HTML
rem   _j_out.txt  = output PNG path
rem   _j_w.txt    = viewport width
rem   _j_h.txt    = viewport height
setlocal
set "WORK=C:\Users\Administrator\.trae-cn\assistant\fund-dca-system\fund-dca-app\brand\outputs\fit_work"
set "CHROME=C:\Program Files\Google\Chrome\Application\chrome.exe"
set "HTML="
set "OUT="
set "W="
set "H="
set /p HTML=<"%WORK%\_j_html.txt"
set /p OUT=<"%WORK%\_j_out.txt"
set /p W=<"%WORK%\_j_w.txt"
set /p H=<"%WORK%\_j_h.txt"
del "%WORK%\_j_done.txt" 2>nul
"%CHROME%" --headless=new --disable-gpu --no-sandbox --hide-scrollbars ^
  --user-data-dir="%WORK%\_cud" --crash-dumps-dir="%WORK%\_crash" ^
  --screenshot="%OUT%" --window-size=%W%,%H% ^
  --default-background-color=FFFFFFFF "%HTML%" ^
  > "%WORK%\_j_out.log" 2> "%WORK%\_j_err.log"
echo EXIT=%errorlevel% > "%WORK%\_j_done.txt"
