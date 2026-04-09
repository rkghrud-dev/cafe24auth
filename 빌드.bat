@echo off
chcp 65001 > nul
echo Cafe24Auth 빌드 중...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
if %errorlevel% == 0 (
    echo.
    echo ✅ 빌드 완료! publish\Cafe24Auth.exe 실행하세요.
    start "" "publish"
) else (
    echo ❌ 빌드 실패
)
pause
