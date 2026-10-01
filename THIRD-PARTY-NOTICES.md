# 외부 구성요소

담아는 NuGet 패키지를 직접 참조하지 않습니다. 아래는 .NET SDK가 빌드·배포 때 함께 쓰는 구성요소입니다.

| 구성요소 | 쓰임 | 라이선스 | GPL-3.0과의 관계 |
|---|---|---|---|
| .NET 런타임 (Microsoft.NETCore.App 10.0) | 배포본에 포함 | MIT | 호환 |
| Windows Desktop 런타임 (WPF, Windows Forms 10.0) | 배포본에 포함 | MIT | 호환 |
| ILLink (Microsoft.NET.ILLink.Tasks 10.0) | 빌드 도구, 배포본에 미포함 | MIT | 해당 없음 |
| Windows SDK .NET 프로젝션 (Microsoft.Windows.SDK.NET.Ref 10.0.19041.57) | Windows OCR·얼굴 감지 API 호출, 배포본에 포함 | Microsoft Windows SDK 라이선스 (포함된 WinRT.Runtime은 CsWinRT, MIT) | Windows API를 쓰기 위한 시스템 라이브러리로 취급 (GPL-3.0 제1조 System Libraries) |
| Windows OCR, 얼굴 감지, WIC, Win32 | 운영체제 기능, 저장소·배포본에 미포함 | Windows 라이선스 | 시스템 라이브러리 |

글꼴은 Windows에 설치된 Segoe UI·맑은 고딕을 사용하며 포함하지 않습니다. 다른 회사의 로고나 이미지는 포함하지 않습니다.
