# CYD PC Monitor

แอป Windows สำหรับแสดงสถานะคอมพิวเตอร์บนบอร์ด CYD ESP32-2432S028R ผ่าน USB รองรับข้อมูล CPU, GPU, RAM, ดิสก์ และเครือข่าย ตั้งค่าได้ 4 หรือ 6 ช่อง

## ติดตั้ง

ดาวน์โหลด `CYD-PC-Monitor-Setup-1.2.0.exe` จาก [Releases](https://github.com/SANG006/CYD-PC-Monitor/releases) รองรับ Windows 10 รุ่น 1809 ขึ้นไปและ Windows 11 แบบ x64 ที่มี .NET Framework 4.7.2 ขึ้นไป หากอัปเกรด ให้ปิดแอปรุ่นเดิมก่อน ตัวติดตั้งจะถอนรุ่นเดิมและเก็บค่าตั้งไว้

1. ต่อบอร์ดด้วยสาย USB ที่ส่งข้อมูลได้ แล้วเลือกพอร์ต COM ของบอร์ด
2. หากบอร์ดยังไม่ได้ลง PC Monitor ให้กด **อัปโหลดเฟิร์มแวร์** และรอจนแถบความคืบหน้าขึ้น 100%
3. เลือกข้อมูลที่จะแสดงแล้วกด **บันทึกการตั้งค่า** หากต้องการให้แอปเปิดหลังลงชื่อเข้า Windows ให้ติ๊ก **เปิดพร้อม Windows**

เฟิร์มแวร์ 1.2 แก้การกระพริบโดยวาดเฉพาะส่วนที่เปลี่ยน โปรแกรมวัดค่า CPU ด้วย Windows Processor Utility

## สร้างจากซอร์ส

- แอป Windows: เรียก `Source/Build.ps1` จาก PowerShell บน Windows เพื่อสร้าง `Windows/CYD-PC-Monitor.exe`
- ตัวติดตั้ง: ติดตั้ง Inno Setup 6 แล้วเรียก `Installer/Build-Installer.ps1 -CompilerPath <path-to-ISCC.exe>` หลังสร้างแอป
- เฟิร์มแวร์: ใช้ `CYD_PCMonitor/CYD_PCMonitor.ino` กับ Arduino ESP32 core 3.3.12 เลือก ESP32 Dev Module, Huge APP, DIO, 40 MHz ติดตั้ง Adafruit ILI9341, Adafruit GFX, Adafruit BusIO, XPT2046_Touchscreen และ ArduinoJson 7

คู่มือในตัวติดตั้งอยู่ที่ [Installer/QuickStart.html](Installer/QuickStart.html) สัญญาอนุญาตสำหรับเครื่องมือและไลบรารีภายนอกอยู่ใน `Windows/Licenses` และ `Windows/FlashTool`
