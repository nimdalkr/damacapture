# 담아

앞으로도 광고가 들어갈 일 없는 오픈소스 윈도우 캡처 프로그램.

## 주요 기능

- **캡처와 가리기**: 영역·창·전체 화면·스크롤 캡처, 모자이크·블러·완전 가리기와 주석.
- **민감 정보 자동 감지**: 얼굴과 이메일·전화번호·카드번호·API 키를 PC 안에서 찾아 한 번에 가립니다.
- **텍스트 추출과 AI에서 편집하기**: Windows 내장 OCR로 글자를 꺼내고, ChatGPT·Claude·Gemini에 붙여넣도록 복사합니다.
- **QR 코드 링크**: 캡처 속 QR 코드를 PC 안에서 읽어 옆에 말풍선으로 띄웁니다. 웹 주소면 브라우저에서 열고, 그 밖의 내용은 복사합니다.

## 지원 환경

Windows 10 1809 이상, x64

## 다운로드

https://fiveovertwo.xyz/ko/products#dama

## 빌드

.NET 10 SDK가 필요합니다.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

자체 검증은 `dotnet bin\Release\net10.0-windows10.0.19041.0\DamaCapture.dll --self-test`를 실행하고 같은 폴더의 `self-test-result.json`을 확인합니다.

## 라이선스

Copyright (C) 2026 Five Over Two

[GPL-3.0](LICENSE). 외부 구성요소는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)를 보세요.

담아·FOT 이름과 마크, ChatGPT·Claude·Gemini 이름은 각 소유자의 상표이며 GPL-3.0 적용 대상이 아닙니다.
