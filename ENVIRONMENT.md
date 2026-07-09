# 환경 설정 (ENVIRONMENT)

이 문서는 환경에 의존적인 설정, 수동 설치 항목, 경로 정보를 모아둔다.
새 환경에서 작업 시 반드시 이 문서를 먼저 확인한다.

---

## 시스템 요구사항

| 항목 | 버전 |
|------|------|
| .NET | 8.0 |
| Unity | 6.3 이상 |
| Git | 2.x 이상 |
| Docker Desktop | 최신 |

---

## Git PATH 설정

Unity Package Manager가 git URL로 패키지를 설치하려면
Git이 시스템 PATH에 등록되어 있어야 한다.

GitHub Desktop을 쓰는 경우 `cmd` 경로만 등록되어 있어서
Unity가 Git을 못 찾을 수 있다. 아래 명령으로 `bin` 경로를 추가한다.

```powershell
# 관리자 권한 PowerShell에서 실행
[System.Environment]::SetEnvironmentVariable(
    "Path",
    $env:Path + ";C:\Program Files\Git\bin",
    [System.EnvironmentVariableTarget]::Machine)
```

실행 후 Unity Hub와 Unity를 완전히 재시작해야 한다.

---

## Unity 프로젝트 환경

### 프로젝트 경로
```
C:\Users\{사용자}\Desktop\Main\AutoBattler
```

### 설치된 패키지 (Package Manager)

| 패키지 | 버전 | 설치 방법 |
|--------|------|-----------|
| Netcode for GameObjects (NGO) | 2.11.0 | Unity Registry |
| YetAnotherHttpHandler | 1.8.0 | git URL |
| YetAnotherHttpHandler (Dependencies) | 0.1.0 | git URL |

**YetAnotherHttpHandler git URL 설치 방법:**

Package Manager → + → Add package from git URL

```
https://github.com/Cysharp/YetAnotherHttpHandler.git?path=src/YetAnotherHttpHandler#1.8.0
```

```
https://github.com/Cysharp/YetAnotherHttpHandler.git?path=src/YetAnotherHttpHandler.Dependencies#1.8.0
```

### 수동 설치 DLL (Assets/Plugins/)

아래 DLL들은 자동으로 설치되지 않으므로 수동으로 넣어야 한다.

| DLL | 출처 | 비고 |
|-----|------|------|
| Google.Protobuf.dll | NuGet: Google.Protobuf 3.25.1 (netstandard2.0) | protobuf 직렬화 |
| Grpc.Net.Client.dll | Grpc.Net.Client.Dependencies.unitypackage (netstandard2.1) | gRPC 클라이언트 |
| Grpc.Core.Api.dll | Grpc.Net.Client.Dependencies.unitypackage (netstandard2.1) | gRPC 코어 |
| Grpc.Net.Common.dll | Grpc.Net.Client.Dependencies.unitypackage (netstandard2.1) | gRPC 공통 |
| Microsoft.Extensions.Logging.Abstractions.dll | Grpc.Net.Client.Dependencies.unitypackage | 로깅 추상화 |
| System.Buffers.dll | Grpc.Net.Client.Dependencies.unitypackage | 버퍼 유틸리티 |
| System.Memory.dll | Grpc.Net.Client.Dependencies.unitypackage | 메모리 유틸리티 |

**Grpc.Net.Client.Dependencies.unitypackage 다운로드:**
```
https://github.com/Cysharp/YetAnotherHttpHandler/releases/tag/redist-20240111-01
```
다운로드 후 Unity에서 Assets → Import Package → Custom Package로 임포트.
"이미 모든 에셋이 있다"는 메시지가 뜨면 Library 폴더를 삭제하고 재시작 후 재시도.

**Google.Protobuf.dll 수동 설치:**
```powershell
# NuGet에서 다운로드
$nugetUrl = "https://www.nuget.org/api/v2/package/Google.Protobuf/3.25.1"
Invoke-WebRequest -Uri $nugetUrl -OutFile "Google.Protobuf.nupkg"

# .nupkg는 zip 형식
Copy-Item "Google.Protobuf.nupkg" "Google.Protobuf.zip"
Expand-Archive "Google.Protobuf.zip" -DestinationPath "Google.Protobuf" -Force

# netstandard2.0 버전을 Unity Plugins 폴더에 복사
Copy-Item "Google.Protobuf\lib\netstandard2.0\Google.Protobuf.dll" `
    "C:\Users\{사용자}\Desktop\Main\AutoBattler\Assets\Plugins\Google.Protobuf.dll"
```

### 네이티브 DLL (Assets/Plugins/x86_64/)

win-x64 전용 네이티브 DLL. Dedicated Server 빌드 시 자동으로 포함된다.

| DLL | 출처 |
|-----|------|
| librdkafka.dll | NuGet: librdkafka.redist 2.6.0 |
| librdkafkacpp.dll | NuGet: librdkafka.redist 2.6.0 |
| libcrypto-3-x64.dll | NuGet: librdkafka.redist 2.6.0 |
| libcurl.dll | NuGet: librdkafka.redist 2.6.0 |
| libssl-3-x64.dll | NuGet: librdkafka.redist 2.6.0 |
| msvcp140.dll | NuGet: librdkafka.redist 2.6.0 |
| vcruntime140.dll | NuGet: librdkafka.redist 2.6.0 |
| zlib1.dll | NuGet: librdkafka.redist 2.6.0 |
| zstd.dll | NuGet: librdkafka.redist 2.6.0 |

**네이티브 DLL 복사 명령:**
```powershell
$unityNative = "C:\Users\{사용자}\Desktop\Main\AutoBattler\Assets\Plugins\x86_64"
$nativeSrc = "C:\Users\{사용자}\.nuget\packages\librdkafka.redist\2.6.0\runtimes\win-x64\native"

Copy-Item "$nativeSrc\*" $unityNative
```

---

## IngameServer.Contracts 빌드 → Unity 자동 복사

`IngameServer.Contracts.csproj`에 빌드 후 이벤트가 설정되어 있어
빌드 시 생성된 `Ingame.cs`, `IngameGrpc.cs`가 자동으로 Unity로 복사된다.

```
복사 대상: Assets\Scripts\Server\Generated\
```

**주의**: 복사 경로가 하드코딩되어 있다.
```
C:\Users\rlawh\Desktop\Main\AutoBattler\Assets\Scripts\Server\Generated
```
다른 환경에서 작업 시 `IngameServer.Contracts.csproj`의 `CopyGeneratedToUnity` 타겟 경로를
실제 환경에 맞게 수정해야 한다.

---

## Docker 컨테이너

DevTools 폴더의 `docker-compose.yml`로 관리한다.

```powershell
cd "C:\Users\{사용자}\Desktop\Main\AutoBattleDevTools"
docker-compose up -d    # 시작
docker-compose down     # 종료
```

| 서비스 | 이미지 | 포트 |
|--------|--------|------|
| kafka | confluentinc/cp-kafka:latest | 9092 |
| rabbitmq | rabbitmq:4-management | 5672, 15672 |

RabbitMQ 관리 콘솔: `http://localhost:15672` (guest/guest)

---

## shared.appsettings.json

Kafka/RabbitMQ 연결 정보를 담은 공유 설정 파일.
위치: `GameServer/Shared/shared.appsettings.json`

각 서버 프로젝트 `csproj`에 빌드 시 출력 폴더로 자동 복사되는 설정이 있다.
자세한 내용은 `GameServer/Shared/README.md` 참고.

---

## 주의사항

- `IngameServer.Contracts.csproj`의 Unity 복사 경로는 절대 경로가 하드코딩되어 있다.
  빌드 시 `ingame.proto`에서 생성된 `Ingame.cs`, `IngameGrpc.cs` 두 파일이
  `Assets\Scripts\Server\Generated\`로 자동 복사된다.
  새 환경에서는 `IngameServer.Contracts.csproj`의 `CopyGeneratedToUnity` 타겟 경로를
  실제 환경에 맞게 수정할 것.
- Unity Plugins 폴더의 DLL들은 Git에 포함되어 있지 않다.
  새 환경에서는 위 지침대로 수동 설치해야 한다.
- `System.Runtime` 버전 충돌 주의: .NET 8 기반 DLL을 Unity Plugins에 넣으면
  Unity Mono 런타임과 충돌한다. 반드시 `netstandard2.0` 또는 `netstandard2.1` 버전을 사용할 것.
