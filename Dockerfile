# ---------- Stage 1: build .NET ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY RoadGuard.CadParser.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish RoadGuard.CadParser.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- Stage 2: runtime (.NET + Python bridge) ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv python3-pip \
    && rm -rf /var/lib/apt/lists/*

# Virtualenv exposes `python` (the backend spawns `python cad_bridge.py ...`)
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH" MPLBACKEND=Agg
COPY requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
# Render injects PORT at runtime; Program.cs binds to it. 10000 is Render's default.
EXPOSE 10000
ENTRYPOINT ["dotnet", "RoadGuard.CadParser.dll"]
