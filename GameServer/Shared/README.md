# Shared 폴더

## 구조

```
Shared/
  shared.appsettings.json      ← Kafka, RabbitMQ 등 공유 설정
  MainServer.Common/           ← 메인 서버(로비/인게임) 공통 코드
  SubServer.Common/            ← 하위 서버(결제/보상/공지 등) 공통 코드
  MasterServer.Contracts/      ← 마스터-메인 서버 간 gRPC 계약(proto)
  README.md                    ← 이 파일
```

---

## shared.appsettings.json이 존재하는 이유

Kafka 브로커 주소, RabbitMQ 호스트/포트 같은 인프라 연결 정보는
**메인 서버와 모든 하위 서버가 동일한 값을 써야 한다.**

각 서버의 `appsettings.json`에 같은 값을 중복으로 적으면:
- 변경 시 모든 파일을 찾아서 고쳐야 한다
- 하나라도 빠뜨리면 연결 실패가 발생하는데 원인을 찾기 어렵다
- 실수로 다른 값을 적는 순간 디버깅이 매우 힘들어진다

그래서 **한 파일에 한 번만 적고, 모든 서버가 그 파일을 읽는** 구조를 선택했다.

### 동작 방식

각 서버의 `.csproj`에 아래가 포함되어 있다.

```xml
<!-- 빌드 시 shared.appsettings.json이 없으면 빌드 실패 -->
<Target Name="CheckSharedConfig" BeforeTargets="Build">
  <Error
    Condition="!Exists('..\..\Shared\shared.appsettings.json')"
    Text="shared.appsettings.json 파일이 없습니다. Shared/ 폴더를 확인하세요." />
</Target>

<!-- 출력 폴더로 자동 복사 -->
<None Include="..\..\Shared\shared.appsettings.json" CopyToOutputDirectory="PreserveNewest" />
```

각 서버의 `Program.cs`에서 이 파일을 설정 소스로 등록한다.

```csharp
builder.Configuration.AddJsonFile("shared.appsettings.json", optional: false);
```

`optional: false`라서 파일이 없으면 실행 자체가 실패한다.
빌드 때 한 번, 실행 때 한 번 — 두 번 체크된다.

---

## 이 방식의 한계 (실전 전환 시 고려할 것)

이 방식은 **단일 머신 개발 환경**에서만 깔끔하게 동작한다.
실제 운영 환경에서는 아래 이유로 한계가 있다.

### 1. 배포 환경마다 값이 다르다
개발(localhost:9092)과 운영(실제 Kafka 클러스터 주소)이 다른데,
파일을 그대로 배포하면 개발용 주소가 운영에 들어갈 위험이 있다.

**→ 전환 방법**: 환경 변수로 오버라이드
.NET은 `appsettings.json` 위에 환경 변수가 덮어쓰는 구조를 기본 지원한다.
```
Kafka__BootstrapServers=실제운영주소:9092
RabbitMQ__Host=실제운영주소
```
운영 배포 시 환경 변수만 주입하면 코드/파일 변경 없이 동작한다.

### 2. 민감한 정보(비밀번호 등)를 파일에 담으면 안 된다
RabbitMQ 비밀번호 같은 값은 소스 코드 저장소에 올라가면 안 된다.

**→ 전환 방법**: 비밀 관리 서비스 사용
- 개발: .NET의 `dotnet user-secrets`
- 운영: AWS Parameter Store, Azure Key Vault, HashiCorp Vault 등

### 3. 서버가 여러 머신에 분산되면 파일 공유가 불가능하다
각 머신이 독립적으로 실행되므로 파일 시스템을 공유할 수 없다.

**→ 전환 방법**: 중앙 설정 서버
HashiCorp Consul, AWS AppConfig, Azure App Configuration 등을 쓰면
모든 서버가 네트워크를 통해 같은 설정을 가져올 수 있다.
설정 변경 시 재배포 없이 실시간 반영도 가능하다.

---

## 요약

| 환경 | 권장 방식 |
|---|---|
| 단일 머신 개발 | shared.appsettings.json (지금 방식) |
| 멀티 환경 (dev/staging/prod) | 환경 변수 오버라이드 추가 |
| 민감 정보 포함 | user-secrets / Vault 연동 |
| 분산 배포 | 중앙 설정 서버 |

지금 구조에서 위로 올라갈수록 코드 변경은 거의 없고
인프라/설정 관리 방식만 바뀐다.
