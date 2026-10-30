FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Travel.Participant.Mock/Travel.Participant.Mock.csproj Travel.Participant.Mock/
RUN dotnet restore Travel.Participant.Mock/Travel.Participant.Mock.csproj
COPY Travel.Participant.Mock/ Travel.Participant.Mock/
RUN dotnet publish Travel.Participant.Mock/Travel.Participant.Mock.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Travel.Participant.Mock.dll"]
