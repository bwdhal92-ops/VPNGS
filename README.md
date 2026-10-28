# VPNGS

تطبيق سطح مكتب (WPF / .NET 10) للاتصال بالإنترنت عبر سيرفرات FlareFeed (VLESS/VMess/Trojan/Hysteria2/TUIC) باستخدام محرك sing-box.

## الميزات
- جلب تلقائي لقائمة السيرفرات من FlareFeed (مع مرايا بديلة).
- فحص استجابة UDP عبر النفق واختيار أفضل سيرفر اجتاز الفحص.
- اتصال بضغطة زر (System Proxy أو TUN Mode).
- تحديث تلقائي كل 60 دقيقة.
- دعم VLESS Reality, Hysteria2, TUIC.

## المتطلبات
- Windows 10/11 (x64)
- .NET 10 SDK (للبناء)
- صلاحيات Administrator (فقط لوضع TUN)

## البناء
```powershell
cd FlareVpn
dotnet build
```

## التشغيل
شغّل `bin\VPNGS\VPNGS.exe` أو ملف `VPNGS.bat` في مجلد المشروع الرئيسي.

الثنائيات (sing-box.exe, wintun.dll) موجودة في `bin/` وتنسخ تلقائياً إلى مجلد الإخراج.

## ملاحظات
- وضع TUN يتطلب wintun.dll (مضمن) وصلاحيات Administrator.
- وضع System Proxy (الافتراضي) لا يتطلب صلاحيات.
