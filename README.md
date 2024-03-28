# AAES
`AAES`는 .NET의 [TAP](https://learn.microsoft.com/ko-kr/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap) 기반 코드에서,
여러 thread의 동시접근이 허용되지 않는 공유 resource를 편리하면서도 고성능으로 다루는 것을 목표로 합니다.

아래 주요 feature들의 앞글자를 따 작명했습니다.


## Asynchronous
resource를 비동기적으로 접근하여 처리합니다.

caller는 resource의 [점유](#Exclusive)를 기다릴 필요 없이 해야 할 일을 던져놓기만 하면 됩니다.

```cs
resource.Access.Then(() =>
{
    // some work
});
// not blocked. continue...
```


## Awatable
원한다면 caller는 처리 결과를 [await](https://learn.microsoft.com/ko-kr/dotnet/csharp/language-reference/operators/await) 할 수 있습니다.

이 기능을 쓰면 마치 동기식 잠금장치처럼 deadlock 발생 가능성이 생기는데,
이런 실수를 조기에 발견하기 위한 [디버깅 기능](./AAES/AAESDebug.cs)도 제공합니다.

```cs
var result = await resource.Access.ThenAsync(() =>
{
    // some work
    return someResult;
});
```

## Exclusive
resource를 배타적으로 점유하여 독립성을 보장하고 원자적인 로직 구현을 가능케 합니다.

점유 후 콜백되는 async 함수의 범위는 [Critical Section](https://en.wikipedia.org/wiki/Critical_section)으로 취급해도 무방합니다.

```cs
resource.Access.Then(async () =>
{
    // begin of critical section
    await someTask;
    // end of critical section
});
```


## Sequential
resource의 접근은 요청(호출)된 순서대로 실행되어야 합니다.

동일한 thread에서 동일한 resource에 대해 여러 건의 접근 요청을 했다면, 그 요청 순서대로 실행되는 것이 보장됩니다.

```cs
string step;
resource.Access.Then(() =>
{
    step = "one";
});
resource.Access.Then(() =>
{
    Debug.Assert(step == "one");
    step = "two";
});
resource.Access.Then(() =>
{
    Debug.Assert(step == "two");
    step = "three";
});
```
