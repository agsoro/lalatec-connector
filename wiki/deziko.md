# Deziko BACnet Implementation Reference (Deep Dive)

This document provides a comprehensive technical reference for the proprietary BACnet implementation used by **Deziko** automation stations, specifically the PXC7-series.

## 1. Object Categories (Property 4941)

Property **4941** is the primary internal classification flag.

| Category ID | Object Role | Description |
| :--- | :--- | :--- |
| **0** | **System/Base** | Core device and hardware definition objects. |
| **1** | **Trendlog** | Historical data buffers (usually COV-based). |
| **2** | **Loop** | Control logic (PID controllers). |
| **3** | **Schedule** | Time-based scheduling logic. |
| **5** | **File/Log** | System files and event logs. |
| **6** | **Point** | Real-time telemetry data points (AI, BI, etc.). |
| **7** | **View** | Structural hierarchy nodes (Structured View). |

---

## 2. Comprehensive Proprietary Property List

Below is the full list of numeric proprietary properties extracted from the PXC7 firmware dump.

### 2.1 Configuration & Fallback (4300-4350)
| ID | Type | Name (Inferred) | Description |
| :--- | :--- | :--- | :--- |
| **4311** | Real | **Subst. Value** | Fallback value for sensor failure. |
| **4312** | Enum | **Subst. Active** | Enable/Disable substitution logic. |
| **4340** | DateTime| **Last Change** | Timestamp of last engineering modification. |

### 2.2 Hierarchy & Naming (4390-4440)
| ID | Type | Name (Inferred) | Description |
| :--- | :--- | :--- | :--- |
| **4393** | String | **Internal Tag** | Technical identifier for the point. |
| **4395** | Enum | **View Priority**| Priority in the user interface. |
| **4397** | Array[Str]| **Naming Path** | Hierarchy segments (Building > Floor > ...). |
| **4398** | Object ID| **Naming Parent** | Reference to parent Structured View. |
| **4435** | Enum | **Visibility** | Controls if the point is visible in User Views. |
| **4436** | Enum | **Grouping** | Internal grouping logic code. |
| **4437** | String | **Short Name** | Functional alias (e.g., `OpModMan`). |
| **4438** | String | **Name Extension**| The canonical short suffix (e.g., `TSu`, `TEx`). |

### 2.3 Alarm & Event Management (4600-4700)
| ID | Type | Name (Inferred) | Description |
| :--- | :--- | :--- | :--- |
| **4635** | Enum | **Alarm Entry 1** | Internal alarm state/index pointer. |
| **4636** | Real | **Alarm Value 1** | Value that triggered the most recent alarm. |

### 2.4 Control & Loop Parameters (4800-4900)
These properties are primarily found on `OBJECT_LOOP` instances.
| ID | Type | Name (Inferred) | Description |
| :--- | :--- | :--- | :--- |
| **4852** | Real | **Proportional Gain**| P-coefficient for control logic. |
| **4853** | Real | **Integral Time** | I-term for control logic. |
| **4854** | Real | **Derivative Time**| D-term for control logic. |
| **4855** | Real | **Sampling Rate** | Control loop cycle time in seconds. |

### 2.5 Operational & Capabilities (4930-5000)
| ID | Type | Name (Inferred) | Description |
| :--- | :--- | :--- | :--- |
| **4930** | Enum | **Op. Status** | Internal runtime status flag. |
| **4941** | Enum | **Object Category**| The functional role ID (see Section 1). |
| **4967** | BitString| **Features** | Mask of supported vendor features. |
| **4968** | Enum | **Model Version** | Internal firmware model identifier. |

### 2.6 Hardware & Commissioning (5000-5120)
| ID | Type | Name (Inferred) | Description |
| :--- | :--- | :--- | :--- |
| **5038** | String | **Hardware Serial**| Serial number of the I/O module. |
| **5040** | Unsigned | **Module ID** | Internal hardware module index. |
| **5054** | String | **Web UI URL** | Link to the local controller web interface. |
| **5092** | String | **I/O Binding** | Physical terminal address (e.g., `IO-1:C=27.6`). |
| **5094** | Unsigned | **Asset ID** | Persistent project-wide unique ID. |
| **5100** | String | **Firmware Hash** | Internal signature of the object configuration. |
| **5102** | Enum | **Health Status** | Internal diagnostic state code. |
| **5103** | String | **Commissioning** | Meta-string (e.g., `SC=1;RC=0;NM=Point;CMNT=OK`). |
| **5107** | Real | **Raw Input** | Unscaled A/D converter value. |

---

## 3. Integration Patterns

### Dynamic Key Generation
The connector uses a tiered priority for telemetry naming:
1.  **Extension (4438)**: `ai_0_tsu_value`
2.  **Path Segment (4397)**: Uses the last element of the array.
3.  **Short Name (4437)**: Uses the functional alias.
4.  **Fallback**: Sanitised `OBJECT_NAME`.

### Hierarchy Crawling
Structured views are navigated via property **209** (`PROP_STRUCTURED_OBJECT_LIST`). Objects that appear in multiple folders will have multiple parent references, but property **4398** always identifies the "Primary Naming Parent".

### Historical Data (Trendlogs)
Deziko Trendlogs (Category 1) store data locally in a ring buffer. The connector can query the `Log_Buffer` property to retrieve data logged during network outages, ensuring 100% data continuity in ThingsBoard.
