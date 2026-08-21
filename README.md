# Cortex Transl

ترجمة حوار الألعاب على Windows أثناء طور القصة. البرنامج يلتقط الشاشة فقط ولا يحقن اللعبة.

## الاستخدام

1. عند أول تشغيل يُحمَّل نموذج عربي محلي (مرة واحدة، بدون API).
2. اختر لغة اللعبة.
3. اضغط **Select dialogue region** (أو `F9`) واسحب حول صندوق الكلام على لقطة الشاشة.
4. شغّل اللعبة **Borderless Windowed**.
5. اضغط **Start** أو `F8`. الترجمة تظهر فوق الصندوق.

DeepL اختياري من شاشة Translate إذا أردت جودة أعلى.

| اختصار | الوظيفة |
| --- | --- |
| `F8` | تشغيل / إيقاف الترجمة |
| `F9` | تحديد مربع الحوار |
| `F10` | فتح البرنامج / إخفاؤه |

## المتطلبات

- Windows 10 (إصدار 2004 / 19041 أو أحدث) أو Windows 11
- لا يحتاج المستخدم تثبيت .NET إذا نشرت النسخة `self-contained` بالأسفل
- اتصال إنترنت لأول تحميل للنموذج العربي فقط (حوالي 30 ميغابايت)

## البناء

```powershell
dotnet restore
dotnet build
dotnet run --project src/CortexTransl.App
```

## نسخة للتثبيت على أجهزة المستخدمين

ابنِ مثبت V1.2.0 (64-بت، معه .NET وملفات العربية إن وُجدت على الجهاز):

```powershell
powershell -ExecutionPolicy Bypass -File pack.ps1
```

المثبت يخرج إلى `dist/CortexTransl-1.2.0-Setup.exe`. ثبّته ثم افتح **Cortex Transl** من قائمة ابدأ.

النشر بدون مثبت:

```powershell
dotnet publish src/CortexTransl.App/CortexTransl.App.csproj -c Release -p:PublishProfile=Win64
```

الملفات تخرج إلى `dist/win-x64/`. لا تستخدم نشر Single File لأن `bergamot.dll` يجب أن تبقى بجانب البرنامج.
