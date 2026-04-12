# Sử dụng image chuẩn của Microsoft dành cho ASP.NET 4.8
FROM mcr.microsoft.com/dotnet/framework/aspnet:4.8

# Đặt thư mục làm việc mặc định bên trong container
WORKDIR /inetpub/wwwroot

# Dọn dẹp sạch sẽ thư mục web mặc định của IIS
RUN powershell -NoProfile -Command Remove-Item -Recurse -Force C:\inetpub\wwwroot\*

# Copy toàn bộ code từ thư mục Publish của bạn vào thẳng IIS trong container
COPY Publish/ /inetpub/wwwroot/

# Mở cổng 80 để web có thể chạy được
EXPOSE 80